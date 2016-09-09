﻿using System;
using kafka_tests.Helpers;
using KafkaNet.Protocol;
using NUnit.Framework;

namespace kafka_tests.Unit
{
    [TestFixture]
    [Category("Unit")]
    public class ProtocolBaseRequestTests
    {
        [Test, Repeat(IntegrationConfig.NumberOfRepeat)]
        public void EnsureHeaderShouldPackCorrectByteLengths()
        {
            var result = KafkaEncoder.EncodeRequestBytes(new RequestContext(123456789, clientId: "test"), new ApiVersionsRequest());

            var withoutLength = new byte[result.Length - 4];
            Buffer.BlockCopy(result, 4, withoutLength, 0, result.Length - 4);
            Assert.That(withoutLength.Length, Is.EqualTo(14));
            Assert.That(withoutLength, Is.EqualTo(new byte[] { 0, 18, 0, 0, 7, 91, 205, 21, 0, 4, 116, 101, 115, 116 }));
        }
    }
}