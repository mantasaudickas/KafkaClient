using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using KafkaClient.Assignment;
using KafkaClient.Common;

namespace KafkaClient.Protocol
{
    public static class KafkaEncoder
    {
        public const int IntegerByteSize = 4;
        public const int CorrelationSize = IntegerByteSize;
        public const int ResponseHeaderSize = IntegerByteSize + CorrelationSize;

        public static T Decode<T>(IRequestContext context, ApiKeyRequestType requstType, ArraySegment<byte> bytes, bool hasSize = false) where T : class, IResponse
        {
            switch (requstType) {
                case ApiKeyRequestType.Produce:
                    return (T)ProduceResponse(context, bytes, hasSize);
                case ApiKeyRequestType.Fetch:
                    return (T)FetchResponse(context, bytes, hasSize);
                case ApiKeyRequestType.Offset:
                    return (T)OffsetResponse(context, bytes, hasSize);
                case ApiKeyRequestType.Metadata:
                    return (T)MetadataResponse(context, bytes, hasSize);
                case ApiKeyRequestType.OffsetCommit:
                    return (T)OffsetCommitResponse(context, bytes, hasSize);
                case ApiKeyRequestType.OffsetFetch:
                    return (T)OffsetFetchResponse(context, bytes, hasSize);
                case ApiKeyRequestType.GroupCoordinator:
                    return (T)GroupCoordinatorResponse(context, bytes, hasSize);
                case ApiKeyRequestType.JoinGroup:
                    return (T)JoinGroupResponse(context, bytes, hasSize);
                case ApiKeyRequestType.Heartbeat:
                    return (T)HeartbeatResponse(context, bytes, hasSize);
                case ApiKeyRequestType.LeaveGroup:
                    return (T)LeaveGroupResponse(context, bytes, hasSize);
                case ApiKeyRequestType.SyncGroup:
                    return (T)SyncGroupResponse(context, bytes, hasSize);
                case ApiKeyRequestType.DescribeGroups:
                    return (T)DescribeGroupsResponse(context, bytes, hasSize);
                case ApiKeyRequestType.ListGroups:
                    return (T)ListGroupsResponse(context, bytes, hasSize);
                case ApiKeyRequestType.SaslHandshake:
                    return (T)SaslHandshakeResponse(context, bytes, hasSize);
                case ApiKeyRequestType.ApiVersions:
                    return (T)ApiVersionsResponse(context, bytes, hasSize);
                case ApiKeyRequestType.CreateTopics:
                    return (T)CreateTopicsResponse(context, bytes, hasSize);
                case ApiKeyRequestType.DeleteTopics:
                    return (T)DeleteTopicsResponse(context, bytes, hasSize);
                default:
                    return default (T);
            }
        }

        #region Encode

        public static ArraySegment<byte> Encode(IRequestContext context, IRequest request)
        {
            switch (request.ApiKey) {
                case ApiKeyRequestType.Produce:
                    return EncodeRequest(context, (ProduceRequest) request);
                case ApiKeyRequestType.Fetch:
                    return EncodeRequest(context, (FetchRequest) request);
                case ApiKeyRequestType.Offset:
                    return EncodeRequest(context, (OffsetRequest) request);
                case ApiKeyRequestType.Metadata:
                    return EncodeRequest(context, (MetadataRequest) request);
                case ApiKeyRequestType.OffsetCommit:
                    return EncodeRequest(context, (OffsetCommitRequest) request);
                case ApiKeyRequestType.OffsetFetch:
                    return EncodeRequest(context, (OffsetFetchRequest) request);
                case ApiKeyRequestType.GroupCoordinator:
                    return EncodeRequest(context, (GroupCoordinatorRequest) request);
                case ApiKeyRequestType.JoinGroup:
                    return EncodeRequest(context, (JoinGroupRequest) request);
                case ApiKeyRequestType.Heartbeat:
                    return EncodeRequest(context, (HeartbeatRequest) request);
                case ApiKeyRequestType.LeaveGroup:
                    return EncodeRequest(context, (LeaveGroupRequest) request);
                case ApiKeyRequestType.SyncGroup:
                    return EncodeRequest(context, (SyncGroupRequest) request);
                case ApiKeyRequestType.DescribeGroups:
                    return EncodeRequest(context, (DescribeGroupsRequest) request);
                case ApiKeyRequestType.ListGroups:
                    return EncodeRequest(context, (ListGroupsRequest) request);
                case ApiKeyRequestType.SaslHandshake:
                    return EncodeRequest(context, (SaslHandshakeRequest) request);
                case ApiKeyRequestType.ApiVersions:
                    return EncodeRequest(context, (ApiVersionsRequest) request);
                case ApiKeyRequestType.CreateTopics:
                    return EncodeRequest(context, (CreateTopicsRequest) request);
                case ApiKeyRequestType.DeleteTopics:
                    return EncodeRequest(context, (DeleteTopicsRequest) request);

                default:
                    using (var writer = EncodeHeader(context, request)) {
                        return writer.ToSegment();
                    }
            }
        }

        private const int MessageHeaderSize = 12;

        /// <summary>
        /// Encodes a collection of messages, in order.
        /// </summary>
        /// <param name="writer">The writer</param>
        /// <param name="messages">The collection of messages to encode together.</param>
        public static IKafkaWriter Write(this IKafkaWriter writer, IEnumerable<Message> messages)
        {
            foreach (var message in messages) {
                writer.Write(0L);
                using (writer.MarkForLength()) {
                    writer.Write(message);
                }
            }
            return writer;
        }

        /// <summary>
        /// Encodes a message object
        /// </summary>
        /// <param name="writer">The writer</param>
        /// <param name="message">Message data to encode.</param>
        /// <returns>Encoded byte[] representation of the message object.</returns>
        /// <remarks>
        /// Format:
        /// Crc (Int32), MagicByte (Byte), Attribute (Byte), Key (Byte[]), Value (Byte[])
        /// </remarks>
        public static IKafkaWriter Write(this IKafkaWriter writer, Message message)
        {
            using (writer.MarkForCrc()) {
                writer.Write(message.MessageVersion)
                      .Write(message.Attribute);
                if (message.MessageVersion >= 1) {
                    writer.Write(message.Timestamp.GetValueOrDefault(DateTimeOffset.UtcNow).ToUnixTimeMilliseconds());
                }
                writer.Write(message.Key)
                      .Write(message.Value);
            }
            return writer;
        }

        /// <summary>
        /// From Documentation:
        /// The replica id indicates the node id of the replica initiating this request. Normal client consumers should always specify this as -1 as they have no node id.
        /// Other brokers set this to be their own node id. The value -2 is accepted to allow a non-broker to issue fetch requests as if it were a replica broker for debugging purposes.
        ///
        /// Kafka Protocol implementation:
        /// https://cwiki.apache.org/confluence/display/KAFKA/A+Guide+To+The+Kafka+Protocol
        /// </summary>
        private const int ReplicaId = -1;

        private static ArraySegment<byte> EncodeRequest(IRequestContext context, ProduceRequest request)
        {
            var totalCompressedBytes = 0;
            var groupedPayloads = (from p in request.Payloads
                                   group p by new
                                   {
                                       p.TopicName,
                                       p.PartitionId,
                                       p.Codec
                                   } into tpc
                                   select tpc).ToList();

            using (var writer = EncodeHeader(context, request)) {
                writer.Write(request.Acks)
                      .Write((int)request.Timeout.TotalMilliseconds)
                      .Write(groupedPayloads.Count);

                foreach (var groupedPayload in groupedPayloads) {
                    var payloads = groupedPayload.ToList();
                    writer.Write(groupedPayload.Key.TopicName)
                          .Write(payloads.Count)
                          .Write(groupedPayload.Key.PartitionId);

                    var compressedBytes = Write(writer, payloads.SelectMany(x => x.Messages), groupedPayload.Key.Codec);
                    Interlocked.Add(ref totalCompressedBytes, compressedBytes);
                }

                var segment = writer.ToSegment();
                context.OnProduceRequestMessages?.Invoke(request.Payloads.Sum(_ => _.Messages.Count), segment.Count, totalCompressedBytes);
                return segment;
            }
        }

        public static int Write(this IKafkaWriter writer, IEnumerable<Message> messages, MessageCodec codec)
        {
            switch (codec) {
                case MessageCodec.CodecNone:
                    using (writer.MarkForLength()) {
                        writer.Write(messages);
                    }
                    return 0;

                case MessageCodec.CodecGzip:
                    using (var messageWriter = new KafkaWriter()) {
                        messageWriter.Write(messages);
                        var messageSet = messageWriter.ToSegment(false);

                        using (writer.MarkForLength()) { // messageset
                            writer.Write(0L); // offset
                            using (writer.MarkForLength()) { // message
                                using (writer.MarkForCrc()) {
                                    writer.Write((byte)0) // message version
                                          .Write((byte)MessageCodec.CodecGzip) // attribute
                                          .Write(-1); // key  -- null, so -1 length
                                    using (writer.MarkForLength()) { // value
                                        var initialPosition = writer.Stream.Position;
                                        Compression.Zip(messageSet, writer.Stream);
                                        var compressedMessageLength = (int)(writer.Stream.Position - initialPosition);
                                        return messageSet.Count - compressedMessageLength;
                                    }
                                }
                            }
                        }
                    }

                default:
                    throw new NotSupportedException($"Codec type of {codec} is not supported.");
            }
        }

        private static ArraySegment<byte> EncodeRequest(IRequestContext context, FetchRequest request)
        {
            using (var writer = EncodeHeader(context, request)) {
                var topicGroups = request.Topics.GroupBy(x => x.TopicName).ToList();
                writer.Write(ReplicaId)
                      .Write((int)Math.Min(int.MaxValue, request.MaxWaitTime.TotalMilliseconds))
                      .Write(request.MinBytes);

                if (context.ApiVersion >= 3) {
                    writer.Write(request.MaxBytes);
                }

                writer.Write(topicGroups.Count);
                foreach (var topicGroup in topicGroups) {
                    var partitions = topicGroup.GroupBy(x => x.PartitionId).ToList();
                    writer.Write(topicGroup.Key)
                          .Write(partitions.Count);

                    foreach (var partition in partitions) {
                        foreach (var fetch in partition) {
                            writer.Write(partition.Key)
                                  .Write(fetch.Offset)
                                  .Write(fetch.MaxBytes);
                        }
                    }
                }

                return writer.ToSegment();
            }
        }

        private static ArraySegment<byte> EncodeRequest(IRequestContext context, OffsetRequest request)
        {
            using (var writer = EncodeHeader(context, request)) {
                var topicGroups = request.Topics.GroupBy(x => x.TopicName).ToList();
                writer.Write(ReplicaId)
                      .Write(topicGroups.Count);

                foreach (var topicGroup in topicGroups) {
                    var partitions = topicGroup.GroupBy(x => x.PartitionId).ToList();
                    writer.Write(topicGroup.Key)
                          .Write(partitions.Count);

                    foreach (var partition in partitions) {
                        foreach (var offset in partition) {
                            writer.Write(partition.Key)
                                  .Write(offset.Timestamp);

                            if (context.ApiVersion == 0) {
                                writer.Write(offset.MaxOffsets);
                            }
                        }
                    }
                }

                return writer.ToSegment();
            }
        }

        private static ArraySegment<byte> EncodeRequest(IRequestContext context, MetadataRequest request)
        {
            using (var writer = EncodeHeader(context, request)) {
                writer.Write(request.Topics, true);

                return writer.ToSegment();
            }
        }

        private static ArraySegment<byte> EncodeRequest(IRequestContext context, OffsetCommitRequest request)
        {
            using (var writer = EncodeHeader(context, request)) {
                writer.Write(request.GroupId);
                if (context.ApiVersion >= 1) {
                    writer.Write(request.GroupGenerationId)
                          .Write(request.MemberId);
                }
                if (context.ApiVersion >= 2) {
                    if (request.OffsetRetention.HasValue) {
                        writer.Write((long) request.OffsetRetention.Value.TotalMilliseconds);
                    } else {
                        writer.Write(-1L);
                    }
                }

                var topicGroups = request.Topics.GroupBy(x => x.TopicName).ToList();
                writer.Write(topicGroups.Count);

                foreach (var topicGroup in topicGroups) {
                    var partitions = topicGroup.GroupBy(x => x.PartitionId).ToList();
                    writer.Write(topicGroup.Key)
                          .Write(partitions.Count);

                    foreach (var partition in partitions) {
                        foreach (var commit in partition) {
                            writer.Write(partition.Key)
                                  .Write(commit.Offset);
                            if (context.ApiVersion == 1) {
                                writer.Write(commit.TimeStamp.GetValueOrDefault(-1));
                            }
                            writer.Write(commit.Metadata);
                        }
                    }
                }
                return writer.ToSegment();
            }
        }

        private static ArraySegment<byte> EncodeRequest(IRequestContext context, OffsetFetchRequest request)
        {
            using (var writer = EncodeHeader(context, request)) {
                var topicGroups = request.Topics.GroupBy(x => x.TopicName).ToList();

                writer.Write(request.GroupId)
                      .Write(topicGroups.Count);

                foreach (var topicGroup in topicGroups) {
                    var partitions = topicGroup.GroupBy(x => x.PartitionId).ToList();
                    writer.Write(topicGroup.Key)
                          .Write(partitions.Count);

                    foreach (var partition in partitions) {
                        foreach (var offset in partition) {
                            writer.Write(offset.PartitionId);
                        }
                    }
                }

                return writer.ToSegment();
            }
        }

        private static ArraySegment<byte> EncodeRequest(IRequestContext context, GroupCoordinatorRequest request)
        {
            using (var writer = EncodeHeader(context, request)) {
                writer.Write(request.GroupId);
                return writer.ToSegment();
            }
        }

        private static ArraySegment<byte> EncodeRequest(IRequestContext context, JoinGroupRequest request)
        {
            using (var writer = EncodeHeader(context, request)) {
                writer.Write(request.GroupId)
                      .Write((int)request.SessionTimeout.TotalMilliseconds);

                if (context.ApiVersion >= 1) {
                    writer.Write((int) request.RebalanceTimeout.TotalMilliseconds);
                }
                writer.Write(request.MemberId)
                      .Write(request.ProtocolType)
                      .Write(request.GroupProtocols.Count);

                var encoder = context.GetEncoder(request.ProtocolType);
                foreach (var protocol in request.GroupProtocols) {
                    writer.Write(protocol.Name)
                          .Write(protocol.Metadata, encoder);
                }

                return writer.ToSegment();
            }
        }

        private static ArraySegment<byte> EncodeRequest(IRequestContext context, HeartbeatRequest request)
        {
            using (var writer = EncodeHeader(context, request)) {
                return writer
                    .Write(request.GroupId)
                    .Write(request.GroupGenerationId)
                    .Write(request.MemberId)
                    .ToSegment();
            }
        }

        private static ArraySegment<byte> EncodeRequest(IRequestContext context, LeaveGroupRequest request)
        {
            using (var writer = EncodeHeader(context, request)) {
                return writer
                    .Write(request.GroupId)
                    .Write(request.MemberId)
                    .ToSegment();
            }
        }

        private static ArraySegment<byte> EncodeRequest(IRequestContext context, SyncGroupRequest request)
        {
            using (var writer = EncodeHeader(context, request)) {
                writer.Write(request.GroupId)
                    .Write(request.GroupGenerationId)
                    .Write(request.MemberId)
                    .Write(request.GroupAssignments.Count);

                var encoder = context.GetEncoder(context.ProtocolType);
                foreach (var assignment in request.GroupAssignments) {
                    writer.Write(assignment.MemberId)
                          .Write(assignment.MemberAssignment, encoder);
                }

                return writer.ToSegment();
            }
        }

        private static ArraySegment<byte> EncodeRequest(IRequestContext context, DescribeGroupsRequest request)
        {
            using (var writer = EncodeHeader(context, request)) {
                writer.Write(request.GroupIds.Count);

                foreach (var groupId in request.GroupIds) {
                    writer.Write(groupId);
                }

                return writer.ToSegment();
            }
        }

        private static ArraySegment<byte> EncodeRequest(IRequestContext context, ListGroupsRequest request)
        {
            using (var writer = EncodeHeader(context, request)) {
                return writer.ToSegment();
            }
        }

        private static ArraySegment<byte> EncodeRequest(IRequestContext context, SaslHandshakeRequest request)
        {
            using (var writer = EncodeHeader(context, request)) {
                writer.Write(request.Mechanism);
                return writer.ToSegment();
            }
        }

        private static ArraySegment<byte> EncodeRequest(IRequestContext context, ApiVersionsRequest request)
        {
            using (var writer = EncodeHeader(context, request)) {
                return writer.ToSegment();
            }
        }

        private static ArraySegment<byte> EncodeRequest(IRequestContext context, CreateTopicsRequest request)
        {
            using (var writer = EncodeHeader(context, request)) {
                writer.Write(request.Topics.Count);
                foreach (var topic in request.Topics) {
                    writer.Write(topic.TopicName)
                          .Write(topic.NumberOfPartitions)
                          .Write(topic.ReplicationFactor)
                          .Write(topic.ReplicaAssignments.Count);
                    foreach (var assignment in topic.ReplicaAssignments) {
                        writer.Write(assignment.PartitionId)
                              .Write(assignment.Replicas);
                    }
                    writer.Write(topic.Configs.Count);
                    foreach (var config in topic.Configs) {
                        writer.Write(config.Key)
                              .Write(config.Value);
                    }
                }
                writer.Write((int)request.Timeout.TotalMilliseconds);
                return writer.ToSegment();
            }
        }

        private static ArraySegment<byte> EncodeRequest(IRequestContext context, DeleteTopicsRequest request)
        {
            using (var writer = EncodeHeader(context, request)) {
                writer.Write(request.Topics, true)
                      .Write((int) request.Timeout.TotalMilliseconds);
                return writer.ToSegment();
            }
        }


        /// <summary>
        /// Encode the common head for kafka request.
        /// </summary>
        /// <remarks>
        /// Request Header => api_key api_version correlation_id client_id 
        ///  api_key => INT16             -- The id of the request type.
        ///  api_version => INT16         -- The version of the API.
        ///  correlation_id => INT32      -- A user-supplied integer value that will be passed back with the response.
        ///  client_id => NULLABLE_STRING -- A user specified identifier for the client making the request.
        /// </remarks>
        private static IKafkaWriter EncodeHeader(IRequestContext context, IRequest request)
        {
            return new KafkaWriter()
                .Write((short)request.ApiKey)
                .Write(context.ApiVersion.GetValueOrDefault())
                .Write(context.CorrelationId)
                .Write(context.ClientId);
        }

        #endregion

        #region Decode

        /// <summary>
        /// Decode a byte[] that represents a collection of messages.
        /// </summary>
        /// <param name="reader">The reader</param>
        /// <param name="codec">The codec of the containing messageset, if any</param>
        /// <returns>Enumerable representing stream of messages decoded from byte[]</returns>
        public static IImmutableList<Message> ReadMessages(this IKafkaReader reader, MessageCodec? codec = null)
        {
            var expectedLength = reader.ReadInt32();
            if (!reader.Available(expectedLength)) throw new BufferUnderRunException($"Message set size of {expectedLength} is not fully available (codec? {codec}).");

            var messages = ImmutableList<Message>.Empty;
            var finalPosition = reader.Position + expectedLength;
            while (reader.Position < finalPosition) {
                // this checks that we have at least the minimum amount of data to retrieve a header
                if (reader.Available(MessageHeaderSize) == false) break;

                var offset = reader.ReadInt64();
                var messageSize = reader.ReadInt32();

                // if the stream does not have enough left in the payload, we got only a partial message
                if (reader.Available(messageSize) == false) throw new BufferUnderRunException($"Message header size of {MessageHeaderSize} is not fully available (codec? {codec}).");

                try {
                    messages = messages.AddRange(reader.ReadMessage(messageSize, offset));
                } catch (EndOfStreamException ex) {
                    throw new BufferUnderRunException($"Message size of {messageSize} is not available (codec? {codec}).", ex);
                }
            }
            return messages;
        }

        /// <summary>
        /// Decode messages from a payload and assign it a given kafka offset.
        /// </summary>
        /// <param name="reader">The reader</param>
        /// <param name="messageSize">The size of the message, for Crc Hash calculation</param>
        /// <param name="offset">The offset represting the log entry from kafka of this message.</param>
        /// <returns>Enumerable representing stream of messages decoded from byte[].</returns>
        /// <remarks>The return type is an Enumerable as the message could be a compressed message set.</remarks>
        public static IImmutableList<Message> ReadMessage(this IKafkaReader reader, int messageSize, long offset)
        {
            var crc = reader.ReadUInt32();
            var crcHash = reader.CrcHash(messageSize - 4);
            if (crc != crcHash) throw new CrcValidationException("Buffer did not match CRC validation.") { Crc = crc, CalculatedCrc = crcHash };

            var messageVersion = reader.ReadByte();
            var attribute = reader.ReadByte();
            DateTimeOffset? timestamp = null;
            if (messageVersion >= 1) {
                var milliseconds = reader.ReadInt64();
                if (milliseconds >= 0) {
                    timestamp = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
                }
            }
            var key = reader.ReadBytes();

            var codec = (MessageCodec)(Message.AttributeMask & attribute);
            switch (codec)
            {
                case MessageCodec.CodecNone: {
                    var value = reader.ReadBytes();
                    return ImmutableList<Message>.Empty.Add(new Message(value, key, attribute, offset, messageVersion, timestamp));
                }

                case MessageCodec.CodecGzip: {
                    var messageLength = reader.ReadInt32();
                    var messageStream = new LimitedReadableStream(reader.Stream, messageLength);
                    using (var gzipReader = new BigEndianBinaryReader(messageStream.Unzip())) {
                        return gzipReader.ReadMessages(codec);
                    }
                }

                default:
                    throw new NotSupportedException($"Codec type of {codec} is not supported.");
            }
        }

        private class LimitedReadableStream : Stream
        {
            private readonly Stream _stream;
            private readonly long _finalPosition;

            public LimitedReadableStream(Stream stream, int maxRead)
            {
                _stream = stream;
                _finalPosition = _stream.Position + maxRead;
            }

            public override void Flush()
            {
                _stream.Flush();
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                throw new NotImplementedException();
            }

            public override void SetLength(long value)
            {
                throw new NotImplementedException();
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                var toRead = Math.Min(count, (int)(_finalPosition - _stream.Position));
                return _stream.Read(buffer, offset, toRead);
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                throw new NotImplementedException();
            }

            public override bool CanRead => _stream.CanRead;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => _stream.Length;

            public override long Position {
                get { throw new NotImplementedException(); }
                set { throw new NotImplementedException(); }
            }
        }

        private static IResponse ProduceResponse(IRequestContext context, ArraySegment<byte> payload, bool hasSize)
        {
            using (var reader = new BigEndianBinaryReader(payload, hasSize ? 8 : 4)) {
                TimeSpan? throttleTime = null;

                var topics = new List<ProduceResponse.Topic>();
                var topicCount = reader.ReadInt32();
                for (var i = 0; i < topicCount; i++) {
                    var topicName = reader.ReadString();

                    var partitionCount = reader.ReadInt32();
                    for (var j = 0; j < partitionCount; j++) {
                        var partitionId = reader.ReadInt32();
                        var errorCode = (ErrorResponseCode) reader.ReadInt16();
                        var offset = reader.ReadInt64();
                        DateTimeOffset? timestamp = null;

                        if (context.ApiVersion >= 2) {
                            var milliseconds = reader.ReadInt64();
                            if (milliseconds >= 0) {
                                timestamp = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
                            }
                        }

                        topics.Add(new ProduceResponse.Topic(topicName, partitionId, errorCode, offset, timestamp));
                    }
                }

                if (context.ApiVersion >= 1) {
                    throttleTime = TimeSpan.FromMilliseconds(reader.ReadInt32());
                }
                return new ProduceResponse(topics, throttleTime);
            }
        }

        private static IResponse FetchResponse(IRequestContext context, ArraySegment<byte> payload, bool hasSize)
        {
            using (var reader = new BigEndianBinaryReader(payload, hasSize ? 8 : 4)) {
                TimeSpan? throttleTime = null;

                if (context.ApiVersion >= 1) {
                    throttleTime = TimeSpan.FromMilliseconds(reader.ReadInt32());
                }

                var topics = new List<FetchResponse.Topic>();
                var topicCount = reader.ReadInt32();
                for (var t = 0; t < topicCount; t++) {
                    var topicName = reader.ReadString();

                    var partitionCount = reader.ReadInt32();
                    for (var p = 0; p < partitionCount; p++) {
                        var partitionId = reader.ReadInt32();
                        var errorCode = (ErrorResponseCode) reader.ReadInt16();
                        var highWaterMarkOffset = reader.ReadInt64();
                        var messages = reader.ReadMessages();

                        topics.Add(new FetchResponse.Topic(topicName, partitionId, highWaterMarkOffset, errorCode, messages));
                    }
                }
                return new FetchResponse(topics, throttleTime);
            }
        }

        private static IResponse OffsetResponse(IRequestContext context, ArraySegment<byte> payload, bool hasSize)
        {
            using (var reader = new BigEndianBinaryReader(payload, hasSize ? 8 : 4)) {
                var topics = new List<OffsetResponse.Topic>();
                var topicCount = reader.ReadInt32();
                for (var t = 0; t < topicCount; t++) {
                    var topicName = reader.ReadString();

                    var partitionCount = reader.ReadInt32();
                    for (var p = 0; p < partitionCount; p++) {
                        var partitionId = reader.ReadInt32();
                        var errorCode = (ErrorResponseCode) reader.ReadInt16();

                        if (context.ApiVersion == 0) {
                            var offsetsCount = reader.ReadInt32();
                            for (var o = 0; o < offsetsCount; o++) {
                                var offset = reader.ReadInt64();
                                topics.Add(new OffsetResponse.Topic(topicName, partitionId, errorCode, offset));
                            }
                        } else {
                            var timestamp = reader.ReadInt64();
                            var offset = reader.ReadInt64();
                            topics.Add(new OffsetResponse.Topic(topicName, partitionId, errorCode, offset, DateTimeOffset.FromUnixTimeMilliseconds(timestamp)));
                        }
                    }
                }
                return new OffsetResponse(topics);
            }
        }

        private static IResponse MetadataResponse(IRequestContext context, ArraySegment<byte> payload, bool hasSize)
        {
            using (var reader = new BigEndianBinaryReader(payload, hasSize ? 8 : 4)) {
                var brokers = new Broker[reader.ReadInt32()];
                for (var b = 0; b < brokers.Length; b++) {
                    var brokerId = reader.ReadInt32();
                    var host = reader.ReadString();
                    var port = reader.ReadInt32();
                    string rack = null;
                    if (context.ApiVersion >= 1) {
                        rack = reader.ReadString();
                    }

                    brokers[b] = new Broker(brokerId, host, port, rack);
                }

                string clusterId = null;
                if (context.ApiVersion >= 2) {
                    clusterId = reader.ReadString();
                }

                int? controllerId = null;
                if (context.ApiVersion >= 1) {
                    controllerId = reader.ReadInt32();
                }

                var topics = new MetadataResponse.Topic[reader.ReadInt32()];
                for (var t = 0; t < topics.Length; t++) {
                    var topicError = (ErrorResponseCode) reader.ReadInt16();
                    var topicName = reader.ReadString();
                    bool? isInternal = null;
                    if (context.ApiVersion >= 1) {
                        isInternal = reader.ReadBoolean();
                    }

                    var partitions = new MetadataResponse.Partition[reader.ReadInt32()];
                    for (var p = 0; p < partitions.Length; p++) {
                        var partitionError = (ErrorResponseCode) reader.ReadInt16();
                        var partitionId = reader.ReadInt32();
                        var leaderId = reader.ReadInt32();

                        var replicaCount = reader.ReadInt32();
                        var replicas = replicaCount.Repeat(reader.ReadInt32).ToArray();

                        var isrCount = reader.ReadInt32();
                        var isrs = isrCount.Repeat(reader.ReadInt32).ToArray();

                        partitions[p] = new MetadataResponse.Partition(partitionId, leaderId, partitionError, replicas, isrs);

                    }
                    topics[t] = new MetadataResponse.Topic(topicName, topicError, partitions, isInternal);
                }

                return new MetadataResponse(brokers, topics, controllerId, clusterId);
            }
        }
        
        private static IResponse OffsetCommitResponse(IRequestContext context, ArraySegment<byte> payload, bool hasSize)
        {
            using (var reader = new BigEndianBinaryReader(payload, hasSize ? 8 : 4)) {
                var topics = new List<TopicResponse>();
                var topicCount = reader.ReadInt32();
                for (var t = 0; t < topicCount; t++) {
                    var topicName = reader.ReadString();

                    var partitionCount = reader.ReadInt32();
                    for (var p = 0; p < partitionCount; p++) {
                        var partitionId = reader.ReadInt32();
                        var errorCode = (ErrorResponseCode) reader.ReadInt16();

                        topics.Add(new TopicResponse(topicName, partitionId, errorCode));
                    }
                }

                return new OffsetCommitResponse(topics);
            }
        }

        private static IResponse OffsetFetchResponse(IRequestContext context, ArraySegment<byte> payload, bool hasSize)
        {
            using (var reader = new BigEndianBinaryReader(payload, hasSize ? 8 : 4)) {
                var topics = new List<OffsetFetchResponse.Topic>();
                var topicCount = reader.ReadInt32();
                for (var t = 0; t < topicCount; t++) {
                    var topicName = reader.ReadString();

                    var partitionCount = reader.ReadInt32();
                    for (var p = 0; p < partitionCount; p++) {
                        var partitionId = reader.ReadInt32();
                        var offset = reader.ReadInt64();
                        var metadata = reader.ReadString();
                        var errorCode = (ErrorResponseCode) reader.ReadInt16();

                        topics.Add(new OffsetFetchResponse.Topic(topicName, partitionId, errorCode, offset, metadata));
                    }
                }

                return new OffsetFetchResponse(topics);
            }
        }
        
        private static IResponse GroupCoordinatorResponse(IRequestContext context, ArraySegment<byte> payload, bool hasSize)
        {
            using (var reader = new BigEndianBinaryReader(payload, hasSize ? 8 : 4)) {
                var errorCode = (ErrorResponseCode)reader.ReadInt16();
                var coordinatorId = reader.ReadInt32();
                var coordinatorHost = reader.ReadString();
                var coordinatorPort = reader.ReadInt32();

                return new GroupCoordinatorResponse(errorCode, coordinatorId, coordinatorHost, coordinatorPort);
            }
        }

        private static IResponse JoinGroupResponse(IRequestContext context, ArraySegment<byte> payload, bool hasSize)
        {
            using (var reader = new BigEndianBinaryReader(payload, hasSize ? 8 : 4)) {
                var errorCode = (ErrorResponseCode)reader.ReadInt16();
                var generationId = reader.ReadInt32();
                var groupProtocol = reader.ReadString();
                var leaderId = reader.ReadString();
                var memberId = reader.ReadString();

                var encoder = context.GetEncoder(context.ProtocolType);
                var members = new JoinGroupResponse.Member[reader.ReadInt32()];
                for (var m = 0; m < members.Length; m++) {
                    var id = reader.ReadString();
                    var metadata = encoder.DecodeMetadata(groupProtocol, reader);
                    members[m] = new JoinGroupResponse.Member(id, metadata);
                }

                return new JoinGroupResponse(errorCode, generationId, groupProtocol, leaderId, memberId, members);
            }
        }

        private static IResponse HeartbeatResponse(IRequestContext context, ArraySegment<byte> payload, bool hasSize)
        {
            using (var reader = new BigEndianBinaryReader(payload, hasSize ? 8 : 4)) {
                var errorCode = (ErrorResponseCode)reader.ReadInt16();

                return new HeartbeatResponse(errorCode);
            }
        }

        private static IResponse LeaveGroupResponse(IRequestContext context, ArraySegment<byte> payload, bool hasSize)
        {
            using (var reader = new BigEndianBinaryReader(payload, hasSize ? 8 : 4)) {
                var errorCode = (ErrorResponseCode)reader.ReadInt16();

                return new LeaveGroupResponse(errorCode);
            }
        }

        private static IResponse SyncGroupResponse(IRequestContext context, ArraySegment<byte> payload, bool hasSize)
        {
            using (var reader = new BigEndianBinaryReader(payload, hasSize ? 8 : 4)) {
                var errorCode = (ErrorResponseCode)reader.ReadInt16();

                var encoder = context.GetEncoder();
                var memberAssignment = encoder.DecodeAssignment(reader);
                return new SyncGroupResponse(errorCode, memberAssignment);
            }
        }

        private static IResponse DescribeGroupsResponse(IRequestContext context, ArraySegment<byte> payload, bool hasSize)
        {
            using (var reader = new BigEndianBinaryReader(payload, hasSize ? 8 : 4)) {
                var groups = new DescribeGroupsResponse.Group[reader.ReadInt32()];
                for (var g = 0; g < groups.Length; g++) {
                    var errorCode = (ErrorResponseCode)reader.ReadInt16();
                    var groupId = reader.ReadString();
                    var state = reader.ReadString();
                    var protocolType = reader.ReadString();
                    var protocol = reader.ReadString();

                    IMembershipEncoder encoder = null;
                    var members = new DescribeGroupsResponse.Member[reader.ReadInt32()];
                    for (var m = 0; m < members.Length; m++) {
                        encoder = encoder ?? context.GetEncoder(protocolType);
                        var memberId = reader.ReadString();
                        var clientId = reader.ReadString();
                        var clientHost = reader.ReadString();
                        var memberMetadata = encoder.DecodeMetadata(protocol, reader);
                        var memberAssignment = encoder.DecodeAssignment(reader);
                        members[m] = new DescribeGroupsResponse.Member(memberId, clientId, clientHost, memberMetadata, memberAssignment);
                    }
                    groups[g] = new DescribeGroupsResponse.Group(errorCode, groupId, state, protocolType, protocol, members);
                }

                return new DescribeGroupsResponse(groups);
            }
        }

        private static IResponse ListGroupsResponse(IRequestContext context, ArraySegment<byte> payload, bool hasSize)
        {
            using (var reader = new BigEndianBinaryReader(payload, hasSize ? 8 : 4)) {
                var errorCode = (ErrorResponseCode)reader.ReadInt16();
                var groups = new ListGroupsResponse.Group[reader.ReadInt32()];
                for (var g = 0; g < groups.Length; g++) {
                    var groupId = reader.ReadString();
                    var protocolType = reader.ReadString();
                    groups[g] = new ListGroupsResponse.Group(groupId, protocolType);
                }

                return new ListGroupsResponse(errorCode, groups);
            }
        }

        private static IResponse SaslHandshakeResponse(IRequestContext context, ArraySegment<byte> payload, bool hasSize)
        {
            using (var reader = new BigEndianBinaryReader(payload, hasSize ? 8 : 4)) {
                var errorCode = (ErrorResponseCode)reader.ReadInt16();
                var enabledMechanisms = new string[reader.ReadInt32()];
                for (var m = 0; m < enabledMechanisms.Length; m++) {
                    enabledMechanisms[m] = reader.ReadString();
                }

                return new SaslHandshakeResponse(errorCode, enabledMechanisms);
            }
        }

        private static IResponse ApiVersionsResponse(IRequestContext context, ArraySegment<byte> payload, bool hasSize)
        {
            using (var reader = new BigEndianBinaryReader(payload, hasSize ? 8 : 4)) {
                var errorCode = (ErrorResponseCode)reader.ReadInt16();

                var apiKeys = new ApiVersionsResponse.VersionSupport[reader.ReadInt32()];
                for (var i = 0; i < apiKeys.Length; i++) {
                    var apiKey = (ApiKeyRequestType)reader.ReadInt16();
                    var minVersion = reader.ReadInt16();
                    var maxVersion = reader.ReadInt16();
                    apiKeys[i] = new ApiVersionsResponse.VersionSupport(apiKey, minVersion, maxVersion);
                }
                return new ApiVersionsResponse(errorCode, apiKeys);
            }
        }        

        private static IResponse CreateTopicsResponse(IRequestContext context, ArraySegment<byte> payload, bool hasSize)
        {
            using (var reader = new BigEndianBinaryReader(payload, hasSize ? 8 : 4)) {
                var topics = new TopicsResponse.Topic[reader.ReadInt32()];
                for (var i = 0; i < topics.Length; i++) {
                    var topicName = reader.ReadString();
                    var errorCode = reader.ReadErrorCode();
                    topics[i] = new TopicsResponse.Topic(topicName, errorCode);
                }
                return new CreateTopicsResponse(topics);
            }
        }        

        private static IResponse DeleteTopicsResponse(IRequestContext context, ArraySegment<byte> payload, bool hasSize)
        {
            using (var reader = new BigEndianBinaryReader(payload, hasSize ? 8 : 4)) {
                var topics = new TopicsResponse.Topic[reader.ReadInt32()];
                for (var i = 0; i < topics.Length; i++) {
                    var topicName = reader.ReadString();
                    var errorCode = reader.ReadErrorCode();
                    topics[i] = new TopicsResponse.Topic(topicName, errorCode);
                }
                return new DeleteTopicsResponse(topics);
            }
        }        

        #endregion
    }
}