﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.Azure.ServiceBus
{
    using System;
    using System.Collections.Generic;
    using System.Threading.Tasks;
    using Microsoft.Azure.Amqp;
    using Microsoft.Azure.ServiceBus.Core;
    using Microsoft.Azure.ServiceBus.Primitives;

    public class TopicClient : ClientEntity, ITopicClient
    {
        readonly bool ownsConnection;
        readonly object syncLock;
        MessageSender innerSender;

        /// <summary>
        /// Instantiates a new <see cref="TopicClient"/> to perform operations on a topic.
        /// </summary>
        /// <param name="connectionStringBuilder"><see cref="ServiceBusConnectionStringBuilder"/> having namespace and topic information.</param>
        /// <param name="retryPolicy">Retry policy for topic operations. Defaults to <see cref="RetryPolicy.Default"/></param>
        public TopicClient(ServiceBusConnectionStringBuilder connectionStringBuilder, RetryPolicy retryPolicy = null)
            : this(connectionStringBuilder.GetNamespaceConnectionString(), connectionStringBuilder.EntityPath, retryPolicy)
        {
        }

        /// <summary>
        /// Instantiates a new <see cref="TopicClient"/> to perform operations on a topic.
        /// </summary>
        /// <param name="connectionString">Namespace connection string. <remarks>Should not contain topic information.</remarks></param>
        /// <param name="entityPath">Path to the topic</param>
        /// <param name="retryPolicy">Retry policy for topic operations. Defaults to <see cref="RetryPolicy.Default"/></param>
        public TopicClient(string connectionString, string entityPath, RetryPolicy retryPolicy = null)
            : this(new ServiceBusNamespaceConnection(connectionString), entityPath, retryPolicy ?? RetryPolicy.Default)
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

        TopicClient(ServiceBusNamespaceConnection serviceBusConnection, string entityPath, RetryPolicy retryPolicy)
            : base($"{nameof(TopicClient)}{GetNextId()}({entityPath})", retryPolicy)
        {
            this.syncLock = new object();
            this.TopicName = entityPath;
            this.ServiceBusConnection = serviceBusConnection;
            this.TokenProvider = TokenProvider.CreateSharedAccessSignatureTokenProvider(
                serviceBusConnection.SasKeyName,
                serviceBusConnection.SasKey);
            this.CbsTokenProvider = new TokenProviderAdapter(this.TokenProvider, serviceBusConnection.OperationTimeout);
        }

        public string TopicName { get; }

        public string Path => this.TopicName;

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
                                this.TopicName,
                                MessagingEntityType.Topic,
                                this.ServiceBusConnection,
                                this.CbsTokenProvider,
                                this.RetryPolicy);
                        }
                    }
                }

                return this.innerSender;
            }
        }

        internal ServiceBusNamespaceConnection ServiceBusConnection { get; }

        ICbsTokenProvider CbsTokenProvider { get; }

        TokenProvider TokenProvider { get; }

        protected override async Task OnClosingAsync()
        {
            if (this.innerSender != null)
            {
                await this.innerSender.CloseAsync().ConfigureAwait(false);
            }

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