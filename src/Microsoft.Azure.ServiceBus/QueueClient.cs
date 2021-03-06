﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.Azure.ServiceBus
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Amqp;
    using Core;
    using Microsoft.Azure.Amqp;
    using Primitives;

    /// <summary>
    /// Anchor class - all Queue client operations start here.
    /// </summary>
    public class QueueClient : ClientEntity, IQueueClient
    {
        readonly bool ownsConnection;
        readonly object syncLock;
        int prefetchCount;
        MessageSender innerSender;
        MessageReceiver innerReceiver;
        AmqpSessionClient sessionClient;
        SessionPumpHost sessionPumpHost;

        /// <summary>
        /// Instantiates a new <see cref="QueueClient"/> to perform operations on a queue.
        /// </summary>
        /// <param name="connectionStringBuilder"><see cref="ServiceBusConnectionStringBuilder"/> having namespace and queue information.</param>
        /// <param name="receiveMode">Mode of receive of messages. Defaults to <see cref="ReceiveMode"/>.PeekLock.</param>
        /// <param name="retryPolicy">Retry policy for queue operations. Defaults to <see cref="RetryPolicy.Default"/></param>
        public QueueClient(ServiceBusConnectionStringBuilder connectionStringBuilder, ReceiveMode receiveMode = ReceiveMode.PeekLock, RetryPolicy retryPolicy = null)
            : this(connectionStringBuilder.GetNamespaceConnectionString(), connectionStringBuilder.EntityPath, receiveMode, retryPolicy)
        {
        }

        /// <summary>
        /// Instantiates a new <see cref="QueueClient"/> to perform operations on a queue.
        /// </summary>
        /// <param name="connectionString">Namespace connection string. <remarks>Should not contain queue information.</remarks></param>
        /// <param name="entityPath">Path to the queue</param>
        /// <param name="receiveMode">Mode of receive of messages. Defaults to <see cref="ReceiveMode"/>.PeekLock.</param>
        /// <param name="retryPolicy">Retry policy for queue operations. Defaults to <see cref="RetryPolicy.Default"/></param>
        public QueueClient(string connectionString, string entityPath, ReceiveMode receiveMode = ReceiveMode.PeekLock, RetryPolicy retryPolicy = null)
            : this(new ServiceBusNamespaceConnection(connectionString), entityPath, receiveMode, retryPolicy ?? RetryPolicy.Default)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw Fx.Exception.ArgumentNullOrWhiteSpace(connectionString);
            }
            if (string.IsNullOrWhiteSpace(entityPath))
            {
                throw Fx.Exception.ArgumentNullOrWhiteSpace(entityPath);
            }

            this.ownsConnection = true;
        }

        QueueClient(ServiceBusNamespaceConnection serviceBusConnection, string entityPath, ReceiveMode receiveMode, RetryPolicy retryPolicy)
            : base($"{nameof(QueueClient)}{ClientEntity.GetNextId()}({entityPath})", retryPolicy)
        {
            this.syncLock = new object();
            this.QueueName = entityPath;
            this.ReceiveMode = receiveMode;
            this.ServiceBusConnection = serviceBusConnection;
            this.TokenProvider = TokenProvider.CreateSharedAccessSignatureTokenProvider(
                serviceBusConnection.SasKeyName,
                serviceBusConnection.SasKey);
            this.CbsTokenProvider = new TokenProviderAdapter(this.TokenProvider, serviceBusConnection.OperationTimeout);
        }

        public string QueueName { get; }

        public ReceiveMode ReceiveMode { get; }

        public string Path => this.QueueName;

        /// <summary>
        /// Gets or sets the number of messages that the queue client can simultaneously request.
        /// </summary>
        /// <value>The number of messages that the queue client can simultaneously request.</value>
        public int PrefetchCount
        {
            get => this.prefetchCount;
            set
            {
                if (value < 0)
                {
                    throw Fx.Exception.ArgumentOutOfRange(nameof(this.PrefetchCount), value, "Value cannot be less than 0.");
                }
                this.prefetchCount = value;
                if (this.innerReceiver != null)
                {
                    this.innerReceiver.PrefetchCount = value;
                }
            }
        }

        internal MessageSender InnerSender
        {
            get
            {
                if (this.innerSender == null)
                {
                    lock (this.syncLock)
                    {
                        if (this.innerSender == null)
                        {
                            this.innerSender = new MessageSender(
                                this.QueueName,
                                MessagingEntityType.Queue,
                                this.ServiceBusConnection,
                                this.CbsTokenProvider,
                                this.RetryPolicy);
                        }
                    }
                }

                return this.innerSender;
            }
        }

        internal MessageReceiver InnerReceiver
        {
            get
            {
                if (this.innerReceiver == null)
                {
                    lock (this.syncLock)
                    {
                        if (this.innerReceiver == null)
                        {
                            this.innerReceiver = new MessageReceiver(
                                this.QueueName,
                                MessagingEntityType.Queue,
                                this.ReceiveMode,
                                this.ServiceBusConnection,
                                this.CbsTokenProvider,
                                this.RetryPolicy,
                                this.PrefetchCount);
                        }
                    }
                }

                return this.innerReceiver;
            }
        }

        internal AmqpSessionClient SessionClient
        {
            get
            {
                if (this.sessionClient == null)
                {
                    lock (this.syncLock)
                    {
                        if (this.sessionClient == null)
                        {
                            this.sessionClient = new AmqpSessionClient(
                                this.ClientId,
                                this.Path,
                                MessagingEntityType.Queue,
                                this.ReceiveMode,
                                this.PrefetchCount,
                                this.ServiceBusConnection,
                                this.CbsTokenProvider,
                                this.RetryPolicy);
                        }
                    }
                }

                return this.sessionClient;
            }
        }

        internal SessionPumpHost SessionPumpHost
        {
            get
            {
                if (this.sessionPumpHost == null)
                {
                    lock (this.syncLock)
                    {
                        if (this.sessionPumpHost == null)
                        {
                            this.sessionPumpHost = new SessionPumpHost(
                                this.ClientId,
                                this.ReceiveMode,
                                this.SessionClient);
                        }
                    }
                }

                return this.sessionPumpHost;
            }
        }

        internal ServiceBusConnection ServiceBusConnection { get; }

        ICbsTokenProvider CbsTokenProvider { get; }

        TokenProvider TokenProvider { get; }

        protected override async Task OnClosingAsync()
        {
            if (this.innerSender != null)
            {
                await this.innerSender.CloseAsync().ConfigureAwait(false);
            }

            if (this.innerReceiver != null)
            {
                await this.innerReceiver.CloseAsync().ConfigureAwait(false);
            }

            this.sessionPumpHost?.Close();

            if (this.ownsConnection)
            {
                await this.ServiceBusConnection.CloseAsync().ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Send <see cref="Message"/> to Queue.
        /// <see cref="SendAsync(Message)"/> sends the <see cref="Message"/> to a Service Gateway, which in-turn will forward the Message to the queue.
        /// </summary>
        /// <param name="message">the <see cref="Message"/> to be sent.</param>
        /// <returns>A Task that completes when the send operations is done.</returns>
        public Task SendAsync(Message message)
        {
            return this.SendAsync(new[] { message });
        }

        public Task SendAsync(IList<Message> messageList)
        {
            return this.InnerSender.SendAsync(messageList);
        }

        public Task CompleteAsync(string lockToken)
        {
            return this.InnerReceiver.CompleteAsync(lockToken);
        }

        public Task AbandonAsync(string lockToken)
        {
            return this.InnerReceiver.AbandonAsync(lockToken);
        }

        public Task DeadLetterAsync(string lockToken)
        {
            return this.InnerReceiver.DeadLetterAsync(lockToken);
        }

        /// <summary>Asynchronously processes a message.</summary>
        /// <param name="handler"></param>
        public void RegisterMessageHandler(Func<Message, CancellationToken, Task> handler)
        {
            this.InnerReceiver.RegisterMessageHandler(handler);
        }

        /// <summary>Asynchronously processes a message.</summary>
        /// <param name="handler"></param>
        /// <param name="messageHandlerOptions">Options associated with message pump processing.</param>
        public void RegisterMessageHandler(Func<Message, CancellationToken, Task> handler, MessageHandlerOptions messageHandlerOptions)
        {
            this.InnerReceiver.RegisterMessageHandler(handler, messageHandlerOptions);
        }

        /// <summary>Register a session handler.</summary>
        /// <param name="handler"></param>
        public void RegisterSessionHandler(Func<IMessageSession, Message, CancellationToken, Task> handler)
        {
            var sessionHandlerOptions = new SessionHandlerOptions();
            this.RegisterSessionHandler(handler, sessionHandlerOptions);
        }

        /// <summary>Register a session handler.</summary>
        /// <param name="handler"></param>
        /// <param name="sessionHandlerOptions">Options associated with session pump processing.</param>
        public void RegisterSessionHandler(Func<IMessageSession, Message, CancellationToken, Task> handler, SessionHandlerOptions sessionHandlerOptions)
        {
            this.SessionPumpHost.OnSessionHandlerAsync(handler, sessionHandlerOptions).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Sends a scheduled message
        /// </summary>
        /// <param name="message">Message to be scheduled</param>
        /// <param name="scheduleEnqueueTimeUtc">Time of enqueue</param>
        /// <returns>Sequence number that is needed for cancelling.</returns>
        public Task<long> ScheduleMessageAsync(Message message, DateTimeOffset scheduleEnqueueTimeUtc)
        {
            return this.InnerSender.ScheduleMessageAsync(message, scheduleEnqueueTimeUtc);
        }

        /// <summary>
        /// Cancels a scheduled message
        /// </summary>
        /// <param name="sequenceNumber">Returned on scheduling a message.</param>
        /// <returns></returns>
        public Task CancelScheduledMessageAsync(long sequenceNumber)
        {
            return this.InnerSender.CancelScheduledMessageAsync(sequenceNumber);
        }
    }
}