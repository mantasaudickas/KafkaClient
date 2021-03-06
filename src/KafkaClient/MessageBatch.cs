using System;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using KafkaClient.Common;
using KafkaClient.Protocol;

namespace KafkaClient
{
    public class MessageBatch : IMessageBatch
    {
        public static readonly MessageBatch Empty = new MessageBatch(ImmutableList<Message>.Empty, null, 0L, null, null, false);

        public MessageBatch(ImmutableList<Message> messages, TopicPartition partition, long offset, IRouter router, IConsumerConfiguration configuration, bool autoConsume, int? batchSize = null, string groupId = null, string memberId = null, int generationId = -1)
        {
            _offsetMarked = offset;
            _offsetCommitted = offset;
            _allMessages = messages;
            _batchSize = batchSize;
            Messages = messages.Count > batchSize.GetValueOrDefault(configuration?.BatchSize ?? 0)
                ? messages.GetRange(0, batchSize.GetValueOrDefault(configuration?.BatchSize ?? 0))
                : messages;
            _partition = partition;
            _router = router;
            _configuration = configuration;
            _groupId = groupId;
            _memberId = memberId;
            _generationId = generationId;
            _autoConsume = autoConsume;
        }

        public IImmutableList<Message> Messages { get; }
        private readonly int? _batchSize;
        private readonly ImmutableList<Message> _allMessages;
        private readonly TopicPartition _partition;
        private readonly IRouter _router;
        private readonly IConsumerConfiguration _configuration;
        private readonly string _groupId;
        private readonly string _memberId;
        private readonly int _generationId;
        private readonly bool _autoConsume;
        private long _offsetMarked;
        private long _offsetCommitted;
        private int _disposeCount;

        public async Task<IMessageBatch> FetchNextAsync(CancellationToken cancellationToken)
        {
            if (ReferenceEquals(this, Empty)) return this;

            if (_autoConsume) {
                MarkSuccessful(this.Last());
            }
            var offset = await CommitMarkedAsync(cancellationToken).ConfigureAwait(false);
            var messages = await _router.FetchMessagesAsync(_allMessages, _partition.topic, _partition.partition_id, offset, _configuration, cancellationToken, _batchSize).ConfigureAwait(false);
            return new MessageBatch(messages, _partition, offset, _router, _configuration, _autoConsume, _batchSize);
        }

        public void MarkSuccessful(Message message)
        {
            if (ReferenceEquals(this, Empty) || message == null) return;
            if (_disposeCount > 0) throw new ObjectDisposedException($"The {_partition} batch is disposed.");

            var offset = message.Offset + 1;
            if (_offsetMarked > offset) throw new ArgumentOutOfRangeException(nameof(message), $"Marked offset is {_offsetMarked}, cannot mark previous offset of {offset}.");
            _offsetMarked = message.Offset + 1;
        }

        public async Task<long> CommitMarkedAsync(CancellationToken cancellationToken)
        {
            if (ReferenceEquals(this, Empty)) return 0L;
            if (_disposeCount > 0) throw new ObjectDisposedException($"The {_partition} batch is disposed.");

            var offset = _offsetMarked;
            var committed = _offsetCommitted;
            if (offset <= committed) return committed;

            if (_groupId != null && _memberId != null) {
                var request = new OffsetCommitRequest(_groupId, new[] { new OffsetCommitRequest.Topic(_partition.topic, _partition.partition_id, offset) }, _memberId, _generationId);
                await _router.SendAsync(request, _partition.topic, _partition.partition_id, cancellationToken).ConfigureAwait(false);
            }
            _offsetCommitted = offset;
            return offset;
        }

        public void Dispose()
        {
            if (Interlocked.Increment(ref _disposeCount) != 1) return;
            OnDisposed?.Invoke();
        }

        public Action OnDisposed { get; set; }
    }
}