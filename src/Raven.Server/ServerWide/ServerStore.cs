﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Lucene.Net.Search;
using Raven.Client.Util;
using Raven.Client.Exceptions.Server;
using Raven.Client.Extensions;
using Raven.Client.Http;
using Raven.Client.Json;
using Raven.Client.Server;
using Raven.Server.Commercial;
using Raven.Server.Config;
using Raven.Server.Documents;
using Raven.Server.NotificationCenter;
using Raven.Server.Rachis;
using Raven.Server.NotificationCenter.Notifications;
using Raven.Server.NotificationCenter.Notifications.Details;
using Raven.Server.NotificationCenter.Notifications.Server;
using Raven.Server.ServerWide.Commands;
using Raven.Server.ServerWide.Context;
using Raven.Server.ServerWide.Maintance;
using Raven.Server.Utils;
using Sparrow;
using Sparrow.Json;
using Sparrow.Json.Parsing;
using Voron;
using Sparrow.Logging;
using Sparrow.LowMemory;
using Sparrow.Utils;

namespace Raven.Server.ServerWide
{
    /// <summary>
    /// Persistent store for server wide configuration, such as cluster settings, database configuration, etc
    /// </summary>
    public class ServerStore : IDisposable
    {
        private const string ResourceName = nameof(ServerStore);

        private static readonly Logger Logger = LoggingSource.Instance.GetLogger<ServerStore>(ResourceName);

        private readonly CancellationTokenSource _shutdownNotification = new CancellationTokenSource();

        public CancellationToken ServerShutdown => _shutdownNotification.Token;

        private StorageEnvironment _env;

        private readonly NotificationsStorage _notificationsStorage;

        private RequestExecutor _clusterRequestExecutor;

        public readonly RavenConfiguration Configuration;
        private readonly RavenServer _ravenServer;
        public readonly DatabasesLandlord DatabasesLandlord;
        public readonly NotificationCenter.NotificationCenter NotificationCenter;
        public readonly LicenseManager LicenseManager;
        public readonly FeedbackSender FeedbackSender;

        private readonly TimeSpan _frequencyToCheckForIdleDatabases;

        public ServerStore(RavenConfiguration configuration, RavenServer ravenServer)
        {
            Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _ravenServer = ravenServer;

            DatabasesLandlord = new DatabasesLandlord(this);

            _notificationsStorage = new NotificationsStorage(ResourceName);

            NotificationCenter = new NotificationCenter.NotificationCenter(_notificationsStorage, ResourceName, ServerShutdown);

            LicenseManager = new LicenseManager(NotificationCenter);

            FeedbackSender = new FeedbackSender();

            DatabaseInfoCache = new DatabaseInfoCache();

            _frequencyToCheckForIdleDatabases = Configuration.Databases.FrequencyToCheckForIdle.AsTimeSpan;

        }

        public DatabaseInfoCache DatabaseInfoCache { get; set; }

        public TransactionContextPool ContextPool;

        public long LastRaftCommitEtag
        {
            get
            {
                using (ContextPool.AllocateOperationContext(out TransactionOperationContext context))
                using (context.OpenReadTransaction())
                    return _engine.GetLastCommitIndex(context);
            }
        }

        public ClusterStateMachine Cluster => _engine.StateMachine;
        public string LeaderTag => _engine.LeaderTag;

        public string NodeTag => _engine.Tag;
        public RachisConsensus.State CurrentState => _engine.CurrentState;

        public bool Disposed => _disposed;

        private Timer _timer;
        private RachisConsensus<ClusterStateMachine> _engine;
        private bool _disposed;
        public RachisConsensus<ClusterStateMachine> Engine => _engine;

        private ClusterMaintenanceSupervisor _clusterMaintenanceSupervisor;

        public Dictionary<string, ClusterNodeStatusReport> ClusterStats()
        {
            if (_engine.LeaderTag != NodeTag)
                throw new NotLeadingException($"Stats can be requested only from the raft leader {_engine.LeaderTag}");
            return _clusterMaintenanceSupervisor?.GetStats();
        }

        public async Task ClusterMaintanceSetupTask()
        {
            while (true)
            {
                try
                {
                    if (_engine.LeaderTag != NodeTag)
                    {
                        await _engine.WaitForState(RachisConsensus.State.Leader)
                                     .WithCancellation(_shutdownNotification.Token);
                        continue;
                    }
                    using (_clusterMaintenanceSupervisor = new ClusterMaintenanceSupervisor(this, _engine.Tag, _engine.CurrentTerm))
                    using (new ClusterObserver(this, _clusterMaintenanceSupervisor, _engine, ContextPool, ServerShutdown))
                    {
                        var oldNodes = new Dictionary<string, string>();
                        while (_engine.LeaderTag == NodeTag)
                        {
                            var topologyChangedTask = _engine.GetTopologyChanged();
                            ClusterTopology clusterTopology;
                            using (ContextPool.AllocateOperationContext(out TransactionOperationContext context))
                            using (context.OpenReadTransaction())
                            {
                                clusterTopology = _engine.GetTopology(context);
                            }
                            var newNodes = clusterTopology.AllNodes;
                            var nodesChanges = ClusterTopology.DictionaryDiff(oldNodes, newNodes);
                            oldNodes = newNodes;
                            foreach (var node in nodesChanges.removedValues)
                            {
                                _clusterMaintenanceSupervisor.RemoveFromCluster(node.Key);
                            }
                            foreach (var node in nodesChanges.addedValues)
                            {
                                var task = _clusterMaintenanceSupervisor.AddToCluster(node.Key, clusterTopology.GetUrlFromTag(node.Key)).ContinueWith(t =>
                                        {
                                            if (Logger.IsInfoEnabled)
                                                Logger.Info($"ClusterMaintenanceSupervisor() => Failed to add to cluster node key = {node.Key}", t.Exception);
                                        }, TaskContinuationOptions.OnlyOnFaulted);
                                GC.KeepAlive(task);
                            }

                            var leaderChanged = _engine.WaitForLeaveState(RachisConsensus.State.Leader);
                            if (await Task.WhenAny(topologyChangedTask, leaderChanged)
                                          .WithCancellation(_shutdownNotification.Token) == leaderChanged)
                                break;
                        }
                    }
                }
                catch (TaskCanceledException)
                {// ServerStore dispose?
                    throw;
                }
                catch (Exception)
                {
                    //
                }
            }
        }

        public ClusterTopology GetClusterTopology(TransactionOperationContext context)
        {
            return _engine.GetTopology(context);
        }

        public async Task AddNodeToClusterAsync(string nodeUrl)
        {
            await _engine.AddToClusterAsync(nodeUrl).WithCancellation(_shutdownNotification.Token);
        }

        public async Task RemoveFromClusterAsync(string nodeTag)
        {
            await _engine.RemoveFromClusterAsync(nodeTag).WithCancellation(_shutdownNotification.Token);
        }

        public void Initialize()
        {
            LowMemoryNotification.Initialize(ServerShutdown,
                Configuration.Memory.LowMemoryDetection.GetValue(SizeUnit.Bytes),
                Configuration.Memory.PhysicalRatioForLowMemDetection);

            if (Logger.IsInfoEnabled)
                Logger.Info("Starting to open server store for " + (Configuration.Core.RunInMemory ? "<memory>" : Configuration.Core.DataDirectory.FullPath));

            var path = Configuration.Core.DataDirectory.Combine("System");
            var storeAlertForLateRaise = new List<AlertRaised>();

            var options = Configuration.Core.RunInMemory
                ? StorageEnvironmentOptions.CreateMemoryOnly()
                : StorageEnvironmentOptions.ForPath(path.FullPath);

            options.OnNonDurableFileSystemError += (obj, e) =>
            {
                var alert = AlertRaised.Create("Non Durable File System - System Storage",
                    e.Message,
                    AlertType.NonDurableFileSystem,
                    NotificationSeverity.Warning,
                    "NonDurable Error System",
                    details: new MessageDetails { Message = e.Details });
                if (NotificationCenter.IsInitialized)
                {
                    NotificationCenter.Add(alert);
                }
                else
                {
                    storeAlertForLateRaise.Add(alert);
                }
            };

            options.OnRecoveryError += (obj, e) =>
            {
                var alert = AlertRaised.Create("Recovery Error - System Storage",
                    e.Message,
                    AlertType.NonDurableFileSystem,
                    NotificationSeverity.Error,
                    "Recovery Error System");
                if (NotificationCenter.IsInitialized)
                {
                    NotificationCenter.Add(alert);
                }
                else
                {
                    storeAlertForLateRaise.Add(alert);
                }
            };

            options.SchemaVersion = 2;
            options.ForceUsing32BitsPager = Configuration.Storage.ForceUsing32BitsPager;
            try
            {
                StorageEnvironment.MaxConcurrentFlushes = Configuration.Storage.MaxConcurrentFlushes;

                try
                {
                    _env = new StorageEnvironment(options);
                }
                catch (Exception e)
                {
                    throw new ServerLoadFailureException("Failed to load system storage " + Environment.NewLine + $"At {options.BasePath}", e);
                }
            }
            catch (Exception e)
            {
                if (Logger.IsOperationsEnabled)
                    Logger.Operations(
                        "Could not open server store for " + (Configuration.Core.RunInMemory ? "<memory>" : Configuration.Core.DataDirectory.FullPath), e);
                options.Dispose();
                throw;
            }

            if (Configuration.Queries.MaxClauseCount != null)
                BooleanQuery.MaxClauseCount = Configuration.Queries.MaxClauseCount.Value;

            ContextPool = new TransactionContextPool(_env);


            _engine = new RachisConsensus<ClusterStateMachine>();
            _engine.Initialize(_env, Configuration.Cluster);

            _engine.StateMachine.DatabaseChanged += DatabasesLandlord.ClusterOnDatabaseChanged;
            _engine.StateMachine.DatabaseChanged += OnDatabaseChanged;

            _engine.TopologyChanged += OnTopologyChanged;

            _timer = new Timer(IdleOperations, null, _frequencyToCheckForIdleDatabases, TimeSpan.FromDays(7));
            _notificationsStorage.Initialize(_env, ContextPool);
            DatabaseInfoCache.Initialize(_env, ContextPool);

            NotificationCenter.Initialize();
            foreach (var alertRaised in storeAlertForLateRaise)
            {
                NotificationCenter.Add(alertRaised);
            }
            LicenseManager.Initialize(_env, ContextPool);

            using (ContextPool.AllocateOperationContext(out TransactionOperationContext context))
            {
                context.OpenReadTransaction();
                foreach (var db in _engine.StateMachine.ItemsStartingWith(context, "db/", 0, int.MaxValue))
                {
                    DatabasesLandlord.ClusterOnDatabaseChanged(this, (db.Item1, 0, "Init"));
                }
            }

            Task.Run(ClusterMaintanceSetupTask, ServerShutdown);
        }

        private void OnTopologyChanged(object sender, ClusterTopology topologyJson)
        {
            NotificationCenter.Add(ClusterTopologyChanged.Create(topologyJson, LeaderTag, NodeTag));
        }

        private void OnDatabaseChanged(object sender, (string dbName, long index, string type) t)
        {
            switch (t.type)
            {
                case nameof(DeleteDatabaseCommand):
                    NotificationCenter.Add(DatabaseChanged.Create(t.dbName, DatabaseChangeType.Delete));
                    break;
                case nameof(AddDatabaseCommand):
                    NotificationCenter.Add(DatabaseChanged.Create(t.dbName, DatabaseChangeType.Put));
                    break;
                case nameof(UpdateTopologyCommand):
                    NotificationCenter.Add(DatabaseChanged.Create(t.dbName, DatabaseChangeType.Update));
                    break;
            }

            //TODO: send different commands to studio when necessary
        }

        public IEnumerable<string> GetSecretKeysNames(TransactionOperationContext context)
        {
            var tree = context.Transaction.InnerTransaction.ReadTree("SecretKeys");
            if (tree == null)
                yield break;

            using (var it = tree.Iterate(prefetch: false))
            {
                if (it.Seek(Slices.BeforeAllKeys) == false)
                    yield break;
                do
                {

                    yield return it.CurrentKey.ToString();

                } while (it.MoveNext());
            }

        }

        public unsafe void PutSecretKey(
            TransactionOperationContext context,
            string name,
            byte[] key,
            bool overwrite = false /*Be careful with this one, overwriting a key might be disastrous*/)
        {
            Debug.Assert(context.Transaction != null);
            if (key.Length != 256 / 8)
                throw new ArgumentException($"Key size must be 256 bits, but was {key.Length * 8}", nameof(key));

            byte[] existingKey;
            try
            {
                existingKey = GetSecretKey(context, name);
            }
            catch (Exception)
            {
                // failure to read the key might be because the user password has changed
                // in this case, we ignore the existence of the key and overwrite it
                existingKey = null;
            }
            if (existingKey != null)
            {
                fixed (byte* pKey = key)
                fixed (byte* pExistingKey = existingKey)
                {
                    bool areEqual = Sparrow.Memory.Compare(pKey, pExistingKey, key.Length) == 0;
                    Sodium.ZeroMemory(pExistingKey, key.Length);
                    if (areEqual)
                    {
                        Sodium.ZeroMemory(pKey, key.Length);
                        return;
                    }
                }
            }

            var tree = context.Transaction.InnerTransaction.CreateTree("SecretKeys");
            var record = Cluster.ReadDatabase(context, name);

            if (overwrite == false && tree.Read(name) != null)
                throw new InvalidOperationException($"Attempt to overwrite secret key {name}, which isn\'t permitted (you\'ll lose access to the encrypted db).");

            if (record != null && record.Encrypted == false)
                throw new InvalidOperationException($"Cannot modify key {name} where there is an existing database that is not encrypted");

            var hashLen = Sodium.crypto_generichash_bytes_max();
            var hash = new byte[hashLen + key.Length];
            fixed (byte* pHash = hash)
            fixed (byte* pKey = key)
            {
                try
                {
                    if (Sodium.crypto_generichash(pHash, (UIntPtr)hashLen, pKey, (ulong)key.Length, null, UIntPtr.Zero) != 0)
                        throw new InvalidOperationException("Failed to hash key");

                    Sparrow.Memory.Copy(pHash + hashLen, pKey, key.Length);

                    var entropy = Sodium.GenerateRandomBuffer(256);

                    var protectedData = SecretProtection.Protect(hash, entropy);

                    var ms = new MemoryStream();
                    ms.Write(entropy, 0, entropy.Length);
                    ms.Write(protectedData, 0, protectedData.Length);
                    ms.Position = 0;

                    tree.Add(name, ms);
                }
                finally
                {
                    Sodium.ZeroMemory(pHash, hash.Length);
                    Sodium.ZeroMemory(pKey, key.Length);
                }
            }
        }


        public unsafe byte[] GetSecretKey(TransactionOperationContext context, string name)
        {
            Debug.Assert(context.Transaction != null);

            var tree = context.Transaction.InnerTransaction.ReadTree("SecretKeys");

            var readResult = tree?.Read(name);
            if (readResult == null)
                return null;

            const int numberOfBits = 256;
            var entropy = new byte[numberOfBits / 8];
            var reader = readResult.Reader;
            reader.Read(entropy, 0, entropy.Length);
            var protectedData = new byte[reader.Length - entropy.Length];
            reader.Read(protectedData, 0, protectedData.Length);

            var data = SecretProtection.Unprotect(protectedData, entropy);

            var hashLen = Sodium.crypto_generichash_bytes_max();

            fixed (byte* pData = data)
            fixed (byte* pHash = new byte[hashLen])
            {
                try
                {
                    if (Sodium.crypto_generichash(pHash, (UIntPtr)hashLen, pData + hashLen, (ulong)(data.Length - hashLen), null, UIntPtr.Zero) != 0)
                        throw new InvalidOperationException($"Unable to compute hash for {name}");

                    if (Sodium.sodium_memcmp(pData, pHash, (UIntPtr)hashLen) != 0)
                        throw new InvalidOperationException($"Unable to validate hash after decryption for {name}, user store changed?");

                    var buffer = new byte[data.Length - hashLen];
                    fixed (byte* pBuffer = buffer)
                    {
                        Sparrow.Memory.Copy(pBuffer, pData + hashLen, buffer.Length);
                    }
                    return buffer;
                }
                finally
                {
                    Sodium.ZeroMemory(pData, data.Length);
                }
            }
        }

        public void DeleteSecretKey(TransactionOperationContext context, string name)
        {
            Debug.Assert(context.Transaction != null);

            var record = Cluster.ReadDatabase(context, name);

            if (record != null)
                throw new InvalidOperationException($"Cannot delete key {name} where there is an existing database that require its usage");

            var tree = context.Transaction.InnerTransaction.CreateTree("SecretKeys");

            tree.Delete(name);
        }

        public async Task<(long, BlittableJsonReaderObject)> DeleteDatabaseAsync(string db, bool hardDelete, string fromNode)
        {
            var deleteCommand = new DeleteDatabaseCommand(db)
            {
                HardDelete = hardDelete,
                FromNode = fromNode
            };
            return await SendToLeaderAsync(deleteCommand);
        }

        public async Task<(long, BlittableJsonReaderObject)> ModifyDatabaseWatchers(string dbName, List<DatabaseWatcher> watchers)
        {
            var watcherCommand = new ModifyDatabaseWatchersCommand(dbName)
            {
                Watchers = watchers
            };
            return await SendToLeaderAsync(watcherCommand);
        }

        public async Task<(long, BlittableJsonReaderObject)> ModifyConflictSolverAsync(string dbName, ConflictSolver solver)
        {
            var conflictResolverCommand = new ModifyConflictSolverCommand(dbName)
            {
                Solver = solver
            };
            return await SendToLeaderAsync(conflictResolverCommand);
        }

        public async Task<(long, BlittableJsonReaderObject)> PutValueInClusterAsync(string key, BlittableJsonReaderObject val)
        {
            var putValueCommand = new PutValueCommand
            {
                Name = key,
                Value = val
            };
            return await SendToLeaderAsync(putValueCommand);
        }

        public async Task<(long, BlittableJsonReaderObject)> DeleteValueInClusterAsync(string key)
        {
            var deleteValueCommand = new DeleteValueCommand()
            {
                Name = key
            };
            return await SendToLeaderAsync(deleteValueCommand);
        }

        public async Task<(long, BlittableJsonReaderObject)> ModifyDatabaseExpiration(TransactionOperationContext context, string name, BlittableJsonReaderObject configurationJson)
        {
            var editExpiration = new EditExpirationCommand(JsonDeserializationCluster.ExpirationConfiguration(configurationJson), name);
            return await SendToLeaderAsync(editExpiration);

        }

        public async Task<(long, BlittableJsonReaderObject)> ModifyDatabasePeriodicBackup(TransactionOperationContext context, string name, BlittableJsonReaderObject configurationJson)
        {
            var editPeriodicBackup = new EditPeriodicBackupCommand(JsonDeserializationCluster.PeriodicBackupConfiguration(configurationJson), name);
            return await SendToLeaderAsync(editPeriodicBackup);
        }

        public async Task<(long, BlittableJsonReaderObject)> ModifyDatabaseVersioning(JsonOperationContext context, string name, BlittableJsonReaderObject configurationJson)
        {
            var editVersioning = new EditVersioningCommand(JsonDeserializationCluster.VersioningConfiguration(configurationJson), name);
            return await SendToLeaderAsync(editVersioning);
        }

        public Guid GetServerId()
        {
            return _env.DbId;
        }

        public void Dispose()
        {
            if (_shutdownNotification.IsCancellationRequested || _disposed)
                return;

            lock (this)
            {
                if (_disposed)
                    return;

                try
                {
                    if (_shutdownNotification.IsCancellationRequested)
                        return;

                    _shutdownNotification.Cancel();
                    var toDispose = new List<IDisposable>
                    {
                        _engine,
                        NotificationCenter,
                        LicenseManager,
                        DatabasesLandlord,
                        _env,
                        ContextPool
                    };

                    var exceptionAggregator = new ExceptionAggregator(Logger, $"Could not dispose {nameof(ServerStore)}.");

                    foreach (var disposable in toDispose)
                        exceptionAggregator.Execute(() =>
                        {
                            try
                            {
                                disposable?.Dispose();
                            }
                            catch (ObjectDisposedException)
                            {
                                //we are disposing, so don't care
                            }
                        });

                    exceptionAggregator.Execute(() => _shutdownNotification.Dispose());

                    exceptionAggregator.ThrowIfNeeded();
                }
                finally
                {
                    _disposed = true;
                }
            }


        }

        public void IdleOperations(object state)
        {
            try
            {
                foreach (var db in DatabasesLandlord.DatabasesCache)
                {
                    try
                    {
                        if (db.Value.Status != TaskStatus.RanToCompletion)
                            continue;

                        var database = db.Value.Result;

                        if (DatabaseNeedsToRunIdleOperations(database))
                            database.RunIdleOperations();
                    }

                    catch (Exception e)
                    {
                        if (Logger.IsInfoEnabled)
                            Logger.Info("Error during idle operation run for " + db.Key, e);
                    }
                }

                try
                {
                    var maxTimeDatabaseCanBeIdle = Configuration.Databases.MaxIdleTime.AsTimeSpan;

                    var databasesToCleanup = DatabasesLandlord.LastRecentlyUsed
                       .Where(x => SystemTime.UtcNow - x.Value > maxTimeDatabaseCanBeIdle)
                       .Select(x => x.Key)
                       .ToArray();

                    foreach (var db in databasesToCleanup)
                    {
                        // intentionally inside the loop, so we get better concurrency overall
                        // since shutting down a database can take a while
                        DatabasesLandlord.UnloadDatabase(db, skipIfActiveInDuration: maxTimeDatabaseCanBeIdle, shouldSkip: database => database.Configuration.Core.RunInMemory);
                    }

                }
                catch (Exception e)
                {
                    if (Logger.IsInfoEnabled)
                        Logger.Info("Error during idle operations for the server", e);
                }
            }
            finally
            {
                try
                {
                    _timer.Change(_frequencyToCheckForIdleDatabases, TimeSpan.FromDays(7));
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }

        private static bool DatabaseNeedsToRunIdleOperations(DocumentDatabase database)
        {
            var now = DateTime.UtcNow;

            var envs = database.GetAllStoragesEnvironment();

            var maxLastWork = DateTime.MinValue;

            foreach (var env in envs)
            {
                if (env.Environment.LastWorkTime > maxLastWork)
                    maxLastWork = env.Environment.LastWorkTime;
            }

            return ((now - maxLastWork).TotalMinutes > 5) || ((now - database.LastIdleTime).TotalMinutes > 10);
        }

        public async Task<(long, BlittableJsonReaderObject)> WriteDbAsync(string databaseName, BlittableJsonReaderObject databaseRecord, long? etag, bool encrypted = false)
        {
            var addDatabaseCommand = new AddDatabaseCommand()
            {
                Name = databaseName,
                Etag = etag,
                Encrypted = encrypted,
                Record = databaseRecord
            };
            return await SendToLeaderAsync(addDatabaseCommand);
        }

        public void EnsureNotPassive()
        {
            if (_engine.CurrentState == RachisConsensus.State.Passive)
            {
                _engine.Bootstarp(_ravenServer.WebUrls[0]);
            }
        }

        public Task<(long, BlittableJsonReaderObject)> PutCommandAsync(BlittableJsonReaderObject cmd)
        {
            return _engine.PutAsync(cmd);
        }

        public bool IsLeader()
        {
            return _engine.CurrentState == RachisConsensus.State.Leader;
        }

        public async Task<(long, BlittableJsonReaderObject)> SendToLeaderAsync(CommandBase cmd)
        {
            using (ContextPool.AllocateOperationContext(out TransactionOperationContext context))
            {
                var djv = cmd.ToJson();
                var cmdJson = context.ReadObject(djv, "raft/command");

                return await SendToLeaderAsyncInternal(cmdJson, context);
            }
        }

        public DynamicJsonArray GetClusterErrors()
        {
            return _engine.GetClusterErrorsFromLeader();
        }

        private async Task<(long, BlittableJsonReaderObject)> SendToLeaderAsyncInternal(BlittableJsonReaderObject cmdJson, TransactionOperationContext context)
        {
            //I think it is reasonable to expect timeout twice of error retry
            var timeout = (int)Configuration.Cluster.ClusterOperationTimeout.AsTimeSpan.TotalMilliseconds;
            var timeoutTask = TimeoutManager.WaitFor(timeout, _shutdownNotification.Token);
            while (true)
            {
                ServerShutdown.ThrowIfCancellationRequested();

                if (_engine.CurrentState == RachisConsensus.State.Leader)
                {
                    return await _engine.PutAsync(cmdJson);
                }

                var logChange = _engine.WaitForHeartbeat();


                var cachedLeaderTag = _engine.LeaderTag; // not actually working
                try
                {
                    if (cachedLeaderTag == null)
                    {
                        var completed = await Task.WhenAny(logChange, TimeoutManager.WaitFor(10000, ServerShutdown));

                        if (completed != logChange)
                            throw new TimeoutException("Could not send command to leader because there is no leader, and we timed out waiting for one");

                        continue;
                    }

                    return await SendToNodeAsync(context, cachedLeaderTag, cmdJson);
                }
                catch (Exception ex)
                {
                    if (Logger.IsInfoEnabled)
                        Logger.Info("Tried to send message to leader, retrying", ex);

                    if (_engine.LeaderTag == cachedLeaderTag)
                        throw; // if the leader changed, let's try again
                }

                if (await Task.WhenAny(logChange, timeoutTask) == timeoutTask)
                    ThrowTimeoutException();
            }

        }

        private static void ThrowTimeoutException()
        {
            throw new TimeoutException();
        }

        private async Task<(long, BlittableJsonReaderObject)> SendToNodeAsync(TransactionOperationContext context, string engineLeaderTag, BlittableJsonReaderObject cmd)
        {
            ClusterTopology clusterTopology;
            using (context.OpenReadTransaction())
                clusterTopology = _engine.GetTopology(context);

            if (clusterTopology.Members.TryGetValue(engineLeaderTag, out string leaderUrl) == false)
                throw new InvalidOperationException("Leader " + engineLeaderTag + " was not found in the topology members");

            var command = new PutRaftCommand(context, cmd);

            if (_clusterRequestExecutor == null)
                _clusterRequestExecutor = RequestExecutor.CreateForSingleNode(leaderUrl, "Rachis.Server", clusterTopology.ApiKey);
            else if (_clusterRequestExecutor.Url.Equals(leaderUrl, StringComparison.OrdinalIgnoreCase) == false ||
                     _clusterRequestExecutor.ApiKey?.Equals(clusterTopology.ApiKey) == false)
            {
                _clusterRequestExecutor.Dispose();
                _clusterRequestExecutor = RequestExecutor.CreateForSingleNode(leaderUrl, "Rachis.Server", clusterTopology.ApiKey);
            }

            await _clusterRequestExecutor.ExecuteAsync(command, context, ServerShutdown);

            return (command.Result.ETag, command.Result.Data);
        }

        protected internal class PutRaftCommand : RavenCommand<PutRaftCommandResult>
        {
            private readonly JsonOperationContext _context;
            private readonly BlittableJsonReaderObject _command;
            public override bool IsReadRequest => false;
            public long CommandIndex { get; private set; }

            public PutRaftCommand(JsonOperationContext context, BlittableJsonReaderObject command)
            {
                _context = context;
                _command = context.ReadObject(command, "Raft command");
            }

            public override HttpRequestMessage CreateRequest(ServerNode node, out string url)
            {
                url = $"{node.Url}/rachis/send";

                var request = new HttpRequestMessage
                {
                    Method = HttpMethod.Post,
                    Content = new BlittableJsonContent(stream =>
                    {
                        using (var writer = new BlittableJsonTextWriter(_context, stream))
                        {
                            writer.WriteObject(_command);
                        }
                    })
                };

                return request;
            }

            public override void SetResponse(BlittableJsonReaderObject response, bool fromCache)
            {
                Result = JsonDeserializationCluster.PutRaftCommandResult(response);
            }
        }

        public class PutRaftCommandResult
        {
            public long ETag { get; set; }

            public BlittableJsonReaderObject Data { get; set; }
        }

        public Task WaitForTopology(Leader.TopologyModification state)
        {
            return _engine.WaitForTopology(state);
        }

        public Task WaitForState(RachisConsensus.State state)
        {
            return _engine.WaitForState(state);
        }

        public void ClusterAcceptNewConnection(TcpClient client)
        {
            _engine.AcceptNewConnection(client);
        }

        public async Task WaitForCommitIndexChange(RachisConsensus.CommitIndexModification modification, long value)
        {
            await _engine.WaitForCommitIndexChange(modification, value);
        }

        public string ClusterStatus()
        {
            return _engine.CurrentState + ", " + _engine.LastStateChangeReason;
        }
    }
}