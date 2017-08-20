﻿using Microsoft.Extensions.Logging;
using org.apache.zookeeper;
using Surging.Core.CPlatform.Runtime.Server;
using Surging.Core.CPlatform.Serialization;
using Surging.Core.CPlatform.Support;
using Surging.Core.CPlatform.Support.Implementation;
using Surging.Core.Zookeeper.Configurations;
using Surging.Core.Zookeeper.WatcherProvider;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Surging.Core.Zookeeper
{
    public class ZookeeperServiceCommandManager : ServiceCommandManagerBase, IDisposable
    {
        private ZooKeeper _zooKeeper;
        private readonly ConfigInfo _configInfo;
        private readonly ISerializer<byte[]> _serializer;
        private readonly ILogger<ZookeeperServiceCommandManager> _logger;
        private ServiceCommandDescriptor[] _serviceCommands;
        private readonly ManualResetEvent _connectionWait = new ManualResetEvent(false);

        public ZookeeperServiceCommandManager(ConfigInfo configInfo, ISerializer<byte[]> serializer,
            ISerializer<string> stringSerializer,IServiceEntryManager serviceEntryManager,
            ILogger<ZookeeperServiceCommandManager> logger) : base(stringSerializer, serviceEntryManager)
        {
            _configInfo = configInfo;
            _serializer = serializer;
            _logger = logger;
            CreateZooKeeper().Wait();
        }

       
        /// <summary>
        /// 获取所有可用的服务命令信息。
        /// </summary>
        /// <returns>服务命令集合。</returns>
        public override async Task<IEnumerable<ServiceCommandDescriptor>> GetServiceCommandsAsync()
        {
            await EnterServiceCommands();
            return _serviceCommands;
        }

        /// <summary>
        /// 清空所有的服务命令。
        /// </summary>
        /// <returns>一个任务。</returns>
        public override async Task ClearAsync()
        {
            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation("准备清空所有命令配置。");
            var path = _configInfo.CommandPath;
            var childrens = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);

            var index = 0;
            while (childrens.Any())
            {
                var nodePath = "/" + string.Join("/", childrens);

                if (await _zooKeeper.existsAsync(nodePath) != null)
                {
                    var result = await _zooKeeper.getChildrenAsync(nodePath);
                    if (result?.Children != null)
                    {
                        foreach (var child in result.Children)
                        {
                            var childPath = $"{nodePath}/{child}";
                            if (_logger.IsEnabled(LogLevel.Debug))
                                _logger.LogDebug($"准备删除：{childPath}。");
                            await _zooKeeper.deleteAsync(childPath);
                        }
                    }
                    if (_logger.IsEnabled(LogLevel.Debug))
                        _logger.LogDebug($"准备删除：{nodePath}。");
                    await _zooKeeper.deleteAsync(nodePath);
                }
                index++;
                childrens = childrens.Take(childrens.Length - index).ToArray();
            }
            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation("服务命令配置清空完成。");
        }

        /// <summary>
        /// 设置服务命令。
        /// </summary>
        /// <param name="routes">服务命令集合。</param>
        /// <returns>一个任务。</returns>
        public override async Task SetServiceCommandsAsync(IEnumerable<ServiceCommandDescriptor> serviceCommand)
        {
            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation("准备添加服务命令。");
            await CreateSubdirectory(_configInfo.CommandPath);

            var path = _configInfo.CommandPath;
            if (!path.EndsWith("/"))
                path += "/";

            serviceCommand = serviceCommand.ToArray();

            foreach (var command in serviceCommand)
            {
                var nodePath = $"{path}{command.ServiceId}";
                var nodeData = _serializer.Serialize(command);
                if (await _zooKeeper.existsAsync(nodePath) == null)
                {
                    if (_logger.IsEnabled(LogLevel.Debug))
                        _logger.LogDebug($"节点：{nodePath}不存在将进行创建。");

                    await _zooKeeper.createAsync(nodePath, nodeData, ZooDefs.Ids.OPEN_ACL_UNSAFE, CreateMode.PERSISTENT);
                }
                else
                {
                    if (_logger.IsEnabled(LogLevel.Debug))
                        _logger.LogDebug($"将更新节点：{nodePath}的数据。");

                    var onlineData = (await _zooKeeper.getDataAsync(nodePath)).Data;
                    if (!DataEquals(nodeData, onlineData))
                        await _zooKeeper.setDataAsync(nodePath, nodeData);
                }
            }
            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation("服务命令添加成功。");
        }


        protected override async Task InitServiceCommandsAsync(IEnumerable<ServiceCommandDescriptor> serviceCommands)
        {
            var commands = await GetServiceCommands(serviceCommands.Select(p => p.ServiceId));
            if (commands.Count() == 0)
            {
                await SetServiceCommandsAsync(serviceCommands);
            }
        }
        
        private async Task CreateZooKeeper()
        {
            if (_zooKeeper != null)
                await _zooKeeper.closeAsync();
            _zooKeeper = new ZooKeeper(_configInfo.ConnectionString, (int)_configInfo.SessionTimeout.TotalMilliseconds
               , new ReconnectionWatcher(
                    () =>
                    {
                        _connectionWait.Set();
                    },
                    async () =>
                    {
                        _connectionWait.Reset();
                        await CreateZooKeeper();
                    }));

        }

        private async Task CreateSubdirectory(string path)
        {
            _connectionWait.WaitOne();
            if (await _zooKeeper.existsAsync(path) != null)
                return;

            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation($"节点{path}不存在，将进行创建。");

            var childrens = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            var nodePath = "/";

            foreach (var children in childrens)
            {
                nodePath += children;
                if (await _zooKeeper.existsAsync(nodePath) == null)
                {
                    await _zooKeeper.createAsync(nodePath, null, ZooDefs.Ids.OPEN_ACL_UNSAFE, CreateMode.PERSISTENT);
                }
                nodePath += "/";
            }
        }

        private ServiceCommandDescriptor GetServiceCommand(byte[] data)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug($"准备转换服务命令，配置内容：{Encoding.UTF8.GetString(data)}。");

            if (data == null)
                return null;

            var descriptor = _serializer.Deserialize<byte[], ServiceCommandDescriptor>(data);
            return descriptor;
        }

        private async Task<ServiceCommandDescriptor> GetServiceCommand(string path)
        {
            ServiceCommandDescriptor result = null;
            var watcher = new NodeMonitorWatcher(_zooKeeper, path,
                  (oldData, newData) =>  NodeChange(oldData, newData));
            if (await _zooKeeper.existsAsync(path) != null)
            {
                var data = (await _zooKeeper.getDataAsync(path, watcher)).Data;
                watcher.SetCurrentData(data);
                result = GetServiceCommand(data);
            }
            return result;
        }

        private async Task<ServiceCommandDescriptor[]> GetServiceCommands(IEnumerable<string> childrens)
        {
            var rootPath = _configInfo.CommandPath;
            if (!rootPath.EndsWith("/"))
                rootPath += "/";

            childrens = childrens.ToArray();
            var serviceCommands = new List<ServiceCommandDescriptor>(childrens.Count());

            foreach (var children in childrens)
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                    _logger.LogDebug($"准备从节点：{children}中获取服务命令信息。");

                var nodePath = $"{rootPath}{children}";
                var serviceCommand = await GetServiceCommand(nodePath);
                if (serviceCommand != null)
                    serviceCommands.Add(serviceCommand);
            }
            return serviceCommands.ToArray();
        }

        private async Task EnterServiceCommands()
        {
            if (_serviceCommands != null)
                return;
            _connectionWait.WaitOne();

            var watcher = new ChildrenMonitorWatcher(_zooKeeper, _configInfo.CommandPath,
                async (oldChildrens, newChildrens) => await ChildrenChange(oldChildrens, newChildrens));
            if (await _zooKeeper.existsAsync(_configInfo.CommandPath, watcher) != null)
            {
                var result = await _zooKeeper.getChildrenAsync(_configInfo.CommandPath, watcher);
                var childrens = result.Children.ToArray();
                watcher.SetCurrentData(childrens);
                _serviceCommands = await GetServiceCommands(childrens);
            }
            else
            {
                if (_logger.IsEnabled(LogLevel.Warning))
                    _logger.LogWarning($"无法获取服务命令信息，因为节点：{_configInfo.CommandPath}，不存在。");
                _serviceCommands = new ServiceCommandDescriptor[0];
            }
        }

        private static bool DataEquals(IReadOnlyList<byte> data1, IReadOnlyList<byte> data2)
        {
            if (data1.Count != data2.Count)
                return false;
            for (var i = 0; i < data1.Count; i++)
            {
                var b1 = data1[i];
                var b2 = data2[i];
                if (b1 != b2)
                    return false;
            }
            return true;
        }

        public  void NodeChange(byte[] oldData, byte[] newData)
        {
            if (DataEquals(oldData, newData))
                return;

            var newCommand =  GetServiceCommand(newData);
            //得到旧的服务命令。
            var oldCommand = _serviceCommands.First(i => i.ServiceId == newCommand.ServiceId);

            lock (_serviceCommands)
            {
                //删除旧服务命令，并添加上新的服务命令。
                _serviceCommands =
                    _serviceCommands
                        .Where(i => i.ServiceId!= newCommand.ServiceId)
                        .Concat(new[] { newCommand }).ToArray();
            }
            //触发服务命令变更事件。
            OnChanged(new ServiceCommandChangedEventArgs(newCommand, oldCommand));
        }

        public async Task ChildrenChange(string[] oldChildrens, string[] newChildrens)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug($"最新的节点信息：{string.Join(",", newChildrens)}");

            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug($"旧的节点信息：{string.Join(",", oldChildrens)}");

            //计算出已被删除的节点。
            var deletedChildrens = oldChildrens.Except(newChildrens).ToArray();
            //计算出新增的节点。
            var createdChildrens = newChildrens.Except(oldChildrens).ToArray();

            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation($"需要被删除的服务命令节点：{string.Join(",", deletedChildrens)}");
            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation($"需要被添加的服务命令节点：{string.Join(",", createdChildrens)}");

            //获取新增的服务命令信息。
            var newCommands = (await GetServiceCommands(createdChildrens)).ToArray();

            var routes = _serviceCommands.ToArray();
            lock (_serviceCommands)
            {
                _serviceCommands = _serviceCommands
                    //删除无效的节点服务命令。
                    .Where(i => !deletedChildrens.Contains(i.ServiceId))
                    //连接上新的服务命令。
                    .Concat(newCommands)
                    .ToArray();
            }
            //需要删除的服务命令集合。
            var deletedRoutes = routes.Where(i => deletedChildrens.Contains(i.ServiceId)).ToArray();
            //触发删除事件。
            OnRemoved(deletedRoutes.Select(command => new ServiceCommandEventArgs(command)).ToArray());

            //触发服务命令被创建事件。
            OnCreated(newCommands.Select(command => new ServiceCommandEventArgs(command)).ToArray());

            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation("服务命令数据更新成功。");
        }


        /// <summary>Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.</summary>
        public void Dispose()
        {
            _connectionWait.Dispose();
            _zooKeeper.closeAsync().Wait();
        }

    }
}
