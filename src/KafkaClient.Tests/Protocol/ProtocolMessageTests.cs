﻿using System;
using System.IO;
using System.Linq;
using System.Text;
using KafkaClient.Common;
using KafkaClient.Protocol;
using KafkaClient.Tests.Helpers;
using NUnit.Framework;

namespace KafkaClient.Tests.Protocol
{
    [TestFixture]
    [Category("Unit")]
    public class ProtocolMessageTests
    {
        [Test, Repeat(IntegrationConfig.TestAttempts)]
        public void DecodeMessageShouldThrowWhenCrcFails()
        {
            var testMessage = new Message(value: "kafka test message.", key: "test");

            using (var writer = new KafkaWriter()) {
                writer.Write(testMessage, false);
                var encoded = writer.ToBytesNoLength();
                encoded[0] += 1;
                using (var reader = new BigEndianBinaryReader(encoded)) {
                    Assert.Throws<CrcValidationException>(() => reader.ReadMessage(encoded.Length, 0).First());
                }
            }
        }

        [Test, Repeat(IntegrationConfig.TestAttempts)]
        [TestCase("test key", "test message")]
        [TestCase(null, "test message")]
        [TestCase("test key", null)]
        [TestCase(null, null)]
        public void EnsureMessageEncodeAndDecodeAreCompatible(string key, string value)
        {
            var testMessage = new Message(key: key, value: value);

            using (var writer = new KafkaWriter()) {
                writer.Write(testMessage, false);
                var encoded = writer.ToBytesNoLength();
                using (var reader = new BigEndianBinaryReader(encoded)) {
                    var result = reader.ReadMessage(encoded.Length, 0).First();

                    Assert.That(testMessage.Key, Is.EqualTo(result.Key));
                    Assert.That(testMessage.Value, Is.EqualTo(result.Value));
                }
            }
        }

        [Test, Repeat(IntegrationConfig.TestAttempts)]
        public void EncodeMessageSetEncodesMultipleMessages()
        {
            //expected generated from python library
            var expected = new byte[]
                {
                    0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 16, 45, 70, 24, 62, 0, 0, 0, 0, 0, 1, 49, 0, 0, 0, 1, 48, 0, 0, 0,
                    0, 0, 0, 0, 0, 0, 0, 0, 16, 90, 65, 40, 168, 0, 0, 0, 0, 0, 1, 49, 0, 0, 0, 1, 49, 0, 0, 0, 0, 0, 0,
                    0, 0, 0, 0, 0, 16, 195, 72, 121, 18, 0, 0, 0, 0, 0, 1, 49, 0, 0, 0, 1, 50
                };

            var messages = new[]
                {
                    new Message("0", "1"),
                    new Message("1", "1"),
                    new Message("2", "1")
                };

            using (var writer = new KafkaWriter()) {
                writer.Write(messages, false);
                var result = writer.ToBytesNoLength();
                Assert.That(expected, Is.EqualTo(result));
            }
        }

        [Test, Repeat(IntegrationConfig.TestAttempts)]
        public void DecodeMessageSetShouldHandleResponseWithMaxBufferSizeHit()
        {
            using (var reader = new BigEndianBinaryReader(MessageHelper.FetchResponseMaxBytesOverflow)) {
                //This message set has a truncated message bytes at the end of it
                var result = reader.ReadMessages();

                var message = Encoding.UTF8.GetString(result.First().Value);

                Assert.That(message, Is.EqualTo("test"));
                Assert.That(result.Count, Is.EqualTo(529));
            }
        }

        [Test, Repeat(IntegrationConfig.TestAttempts)]
        public void WhenMessageIsTruncatedThenBufferUnderRunExceptionIsThrown()
        {
            // arrange
            var offset = (Int64)0;
            var message = new Byte[] { };
            var messageSize = message.Length + 1;
            using (var writer = new KafkaWriter()) {
                writer.Write(offset)
                       .Write(messageSize)
                       .Write(message);
                var bytes = writer.ToBytes();
                using (var reader = new BigEndianBinaryReader(bytes)) {
                    // act/assert
                    Assert.Throws<BufferUnderRunException>(() => reader.ReadMessages());
                }
            }
        }

        [Test, Repeat(IntegrationConfig.TestAttempts)]
        public void WhenMessageIsExactlyTheSizeOfBufferThenMessageIsDecoded()
        {
            // arrange
            var expectedPayloadBytes = new Byte[] { 1, 2, 3, 4 };
            var payload = MessageHelper.CreateMessage(0, new Byte[] { 0 }, expectedPayloadBytes);

            // act/assert
            using (var reader = new BigEndianBinaryReader(payload)) {
                var messages = reader.ReadMessages();
                var actualPayload = messages.First().Value;

                // assert
                var expectedPayload = new Byte[] { 1, 2, 3, 4 };
                CollectionAssert.AreEqual(expectedPayload, actualPayload);
            }
        }
    }
}