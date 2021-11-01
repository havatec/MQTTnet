﻿using MQTTnet.Adapter;
using MQTTnet.Protocol;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MQTTnet.Client;
using MQTTnet.Diagnostics;
using MQTTnet.Implementations;
using MQTTnet.Internal;
using MQTTnet.Packets;

namespace MQTTnet.Server
{
    public class MqttServer : Disposable
    {
        readonly MqttServerEventContainer _eventContainer = new MqttServerEventContainer();
        
        readonly ICollection<IMqttServerAdapter> _adapters;
        readonly MqttServerOptions _options;
        readonly IMqttNetLogger _rootLogger;
        readonly MqttNetSourceLogger _logger;

        MqttClientSessionsManager _clientSessionsManager;
        MqttRetainedMessagesManager _retainedMessagesManager;
        MqttServerKeepAliveMonitor _keepAliveMonitor;
        CancellationTokenSource _cancellationTokenSource;

        public MqttServer(MqttServerOptions options, IEnumerable<IMqttServerAdapter> adapters, IMqttNetLogger logger)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            
            if (adapters == null) throw new ArgumentNullException(nameof(adapters));
            _adapters = adapters.ToList();

            if (logger == null) throw new ArgumentNullException(nameof(logger));
            _logger = logger.WithSource(nameof(MqttServer));
            _rootLogger = logger;
        }

        public event Func<EventArgs, Task> StartedAsync
        {
            add => _eventContainer.StartedEvent.AddHandler(value);
            remove => _eventContainer.StartedEvent.RemoveHandler(value);
        }
        
        public event Func<EventArgs, Task> StoppedAsync
        {
            add => _eventContainer.StoppedEvent.AddHandler(value);
            remove => _eventContainer.StoppedEvent.RemoveHandler(value);
        }
        
        public event Func<ValidatingConnectionEventArgs, Task> ValidatingConnectionAsync
        {
            add => _eventContainer.ValidatingConnectionEvent.AddHandler(value);
            remove => _eventContainer.ValidatingConnectionEvent.RemoveHandler(value);
        }
        
        public event Func<ClientConnectedEventArgs, Task> ClientConnectedAsync
        {
            add => _eventContainer.ClientConnectedEvent.AddHandler(value);
            remove => _eventContainer.ClientConnectedEvent.RemoveHandler(value);
        }
        
        public event Func<ClientDisconnectedEventArgs, Task> ClientDisconnectedAsync
        {
            add => _eventContainer.ClientDisconnectedEvent.AddHandler(value);
            remove => _eventContainer.ClientDisconnectedEvent.RemoveHandler(value);
        }
        
        public event Func<InterceptingPacketEventArgs, Task> InterceptingInboundPacketAsync
        {
            add => _eventContainer.InterceptingInboundPacketEvent.AddHandler(value);
            remove => _eventContainer.InterceptingInboundPacketEvent.RemoveHandler(value);
        }
        
        public event Func<InterceptingPacketEventArgs, Task> InterceptingOutboundPacketAsync
        {
            add => _eventContainer.InterceptingOutboundPacketEvent.AddHandler(value);
            remove => _eventContainer.InterceptingOutboundPacketEvent.RemoveHandler(value);
        }
        
        public event Func<InterceptingSubscriptionEventArgs, Task> InterceptingSubscriptionAsync
        {
            add => _eventContainer.InterceptingSubscriptionEvent.AddHandler(value);
            remove => _eventContainer.InterceptingSubscriptionEvent.RemoveHandler(value);
        }
        
        public event Func<InterceptingUnsubscriptionEventArgs, Task> InterceptingUnsubscriptionAsync
        {
            add => _eventContainer.InterceptingUnsubscriptionEvent.AddHandler(value);
            remove => _eventContainer.InterceptingUnsubscriptionEvent.RemoveHandler(value);
        }
        
        public event Func<InterceptingPublishEventArgs, Task> InterceptingPublishAsync
        {
            add => _eventContainer.InterceptingPublishEvent.AddHandler(value);
            remove => _eventContainer.InterceptingPublishEvent.RemoveHandler(value);
        }
        
        public event Func<ClientSubscribedTopicEventArgs, Task> ClientSubscribedTopicAsync
        {
            add => _eventContainer.ClientSubscribedTopicEvent.AddHandler(value);
            remove => _eventContainer.ClientSubscribedTopicEvent.RemoveHandler(value);
        }
        
        public event Func<ClientUnsubscribedTopicEventArgs, Task> ClientUnsubscribedTopicAsync
        {
            add => _eventContainer.ClientUnsubscribedTopicEvent.AddHandler(value);
            remove => _eventContainer.ClientUnsubscribedTopicEvent.RemoveHandler(value);
        }
        
        public event Func<PreparingSessionEventArgs, Task> PreparingSessionAsync
        {
            add => _eventContainer.PreparingSessionEvent.AddHandler(value);
            remove => _eventContainer.PreparingSessionEvent.RemoveHandler(value);
        }

        public event Func<ApplicationMessageNotConsumedEventArgs, Task> ApplicationMessageNotConsumedAsync
        {
            add => _eventContainer.ApplicationMessageNotConsumedEvent.AddHandler(value);
            remove => _eventContainer.ApplicationMessageNotConsumedEvent.RemoveHandler(value);
        }

        public event Func<RetainedMessageChangedEventArgs, Task> RetainedMessageChangedAsync
        {
            add => _eventContainer.RetainedMessageChangedEvent.AddHandler(value);
            remove => _eventContainer.RetainedMessageChangedEvent.RemoveHandler(value);
        }
        
        public event Func<LoadingRetainedMessagesEventArgs, Task> LoadingRetainedMessageAsync
        {
            add => _eventContainer.LoadingRetainedMessagesEvent.AddHandler(value);
            remove => _eventContainer.LoadingRetainedMessagesEvent.RemoveHandler(value);
        }
        
        public event Func<EventArgs, Task> RetainedMessagesClearedAsync
        {
            add => _eventContainer.RetainedMessagesClearedEvent.AddHandler(value);
            remove => _eventContainer.RetainedMessagesClearedEvent.RemoveHandler(value);
        }

        public bool IsStarted => _cancellationTokenSource != null;
        
        public Task<IList<IMqttClientStatus>> GetClientsAsync()
        {
            ThrowIfDisposed();
            ThrowIfNotStarted();

            return _clientSessionsManager.GetClientStatusAsync();
        }

        public Task<IList<IMqttSessionStatus>> GetSessionsAsync()
        {
            ThrowIfDisposed();
            ThrowIfNotStarted();

            return _clientSessionsManager.GetSessionStatusAsync();
        }

        /// <summary>
        /// Gets all currently cached retained messages.
        /// </summary>
        /// <returns>A list of retained messages.</returns>
        public Task<IList<MqttApplicationMessage>> GetRetainedMessagesAsync()
        {
            ThrowIfDisposed();
            ThrowIfNotStarted();

            return _retainedMessagesManager.GetMessages();
        }

        /// <summary>
        /// Deletes all retained messages.
        /// </summary>
        /// <returns>A task.</returns>
        public Task DeleteRetainedMessagesAsync()
        {
            ThrowIfDisposed();
            ThrowIfNotStarted();

            return _retainedMessagesManager?.ClearMessages() ?? PlatformAbstractionLayer.CompletedTask;
        }

        /// <summary>
        /// Updates the specified retained message.
        /// Setting the payload to null or empty will delete the retained message.
        /// </summary>
        /// <param name="retainedMessage">The retained message.</param>
        /// <returns>A task.</returns>
        public Task UpdateRetainedMessageAsync(MqttApplicationMessage retainedMessage)
        {
            if (retainedMessage == null) throw new ArgumentNullException(nameof(retainedMessage));
            
            ThrowIfDisposed();
            ThrowIfNotStarted();

            return _retainedMessagesManager?.UpdateMessage(null, retainedMessage);
        }

        public Task SubscribeAsync(string clientId, ICollection<MqttTopicFilter> topicFilters)
        {
            if (clientId == null) throw new ArgumentNullException(nameof(clientId));
            if (topicFilters == null) throw new ArgumentNullException(nameof(topicFilters));

            foreach (var topicFilter in topicFilters)
            {
                MqttTopicValidator.ThrowIfInvalidSubscribe(topicFilter.Topic);
            }

            ThrowIfDisposed();
            ThrowIfNotStarted();

            return _clientSessionsManager.SubscribeAsync(clientId, topicFilters);
        }

        public Task UnsubscribeAsync(string clientId, ICollection<string> topicFilters)
        {
            if (clientId == null) throw new ArgumentNullException(nameof(clientId));
            if (topicFilters == null) throw new ArgumentNullException(nameof(topicFilters));

            ThrowIfDisposed();
            ThrowIfNotStarted();

            return _clientSessionsManager.UnsubscribeAsync(clientId, topicFilters);
        }

        public async Task<MqttClientPublishResult> PublishAsync(string senderClientId, MqttApplicationMessage applicationMessage, CancellationToken cancellationToken = default)
        {
            if (applicationMessage == null) throw new ArgumentNullException(nameof(applicationMessage));

            ThrowIfDisposed();

            MqttTopicValidator.ThrowIfInvalid(applicationMessage.Topic);

            ThrowIfNotStarted();

            await _clientSessionsManager.DispatchPublishPacket(senderClientId, applicationMessage);

            return new MqttClientPublishResult();
        }
        
        public async Task StartAsync()
        {
            ThrowIfDisposed();
            ThrowIfStarted();
            
            _cancellationTokenSource = new CancellationTokenSource();
            var cancellationToken = _cancellationTokenSource.Token;

            _retainedMessagesManager = new MqttRetainedMessagesManager(_eventContainer, _rootLogger);
            await _retainedMessagesManager.LoadMessages().ConfigureAwait(false);

            _clientSessionsManager = new MqttClientSessionsManager(_options, _retainedMessagesManager, _eventContainer, _rootLogger);
            
            _keepAliveMonitor = new MqttServerKeepAliveMonitor(_options, _clientSessionsManager, _rootLogger);
            _keepAliveMonitor.Start(cancellationToken);

            foreach (var adapter in _adapters)
            {
                adapter.ClientHandler = c => OnHandleClient(c, cancellationToken);
                await adapter.StartAsync(_options, _rootLogger).ConfigureAwait(false);
            }

            _logger.Info("Started.");

            await _eventContainer.StartedEvent.InvokeAsync(EventArgs.Empty).ConfigureAwait(false);
        }

        public async Task StopAsync()
        {
            try
            {
                if (_cancellationTokenSource == null)
                {
                    return;
                }

                _cancellationTokenSource.Cancel(false);

                await _clientSessionsManager.CloseAllConnectionsAsync().ConfigureAwait(false);
                
                foreach (var adapter in _adapters)
                {
                    adapter.ClientHandler = null;
                    await adapter.StopAsync().ConfigureAwait(false);
                }

                _logger.Info("Stopped.");
            }
            finally
            {
                //_clientSessionsManager?.Dispose();
                _clientSessionsManager = null;

                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = null;

                _retainedMessagesManager = null;
            }

            await _eventContainer.StoppedEvent.InvokeAsync(EventArgs.Empty).ConfigureAwait(false);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                StopAsync().GetAwaiter().GetResult();

                foreach (var adapter in _adapters)
                {
                    adapter.Dispose();
                }
            }

            base.Dispose(disposing);
        }

        Task OnHandleClient(IMqttChannelAdapter channelAdapter, CancellationToken cancellationToken)
        {
            return _clientSessionsManager.HandleClientConnectionAsync(channelAdapter, cancellationToken);
        }

        void ThrowIfStarted()
        {
            if (_cancellationTokenSource != null)
            {
                throw new InvalidOperationException("The MQTT server is already started.");
            }
        }

        void ThrowIfNotStarted()
        {
            if (_cancellationTokenSource == null)
            {
                throw new InvalidOperationException("The MQTT server is not started.");
            }
        }
    }
}
