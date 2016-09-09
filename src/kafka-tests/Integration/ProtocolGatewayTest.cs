﻿using kafka_tests.Helpers;
using KafkaNet;
using KafkaNet.Common;
using KafkaNet.Model;
using KafkaNet.Protocol;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace kafka_tests.Integration
{
    [TestFixture]
    [Category("Integration")]
    public class ProtocolGatewayTest
    {
        private readonly KafkaOptions Options = new KafkaOptions(IntegrationConfig.IntegrationUri);

        [Test, Repeat(IntegrationConfig.NumberOfRepeat)]
        public async Task ProtocolGateway()
        {
            int partitionId = 0;
            var router = new BrokerRouter(Options);

            var producer = new Producer(router);
            string messge1 = Guid.NewGuid().ToString();
            var response = await producer.SendMessageAsync(IntegrationConfig.IntegrationTopic, new[] { new Message(messge1) }, 1, null, MessageCodec.CodecNone, partitionId);
            var offset = response.FirstOrDefault().Offset;

            ProtocolGateway protocolGateway = new ProtocolGateway(IntegrationConfig.IntegrationUri);
            var fetch = new Fetch(IntegrationConfig.IntegrationTopic, partitionId, offset, 32000);

            var fetchRequest = new FetchRequest(fetch, minBytes: 10);

            var r = await protocolGateway.SendProtocolRequest(fetchRequest, IntegrationConfig.IntegrationTopic, partitionId);
            //  var r1 = await protocolGateway.SendProtocolRequest(fetchRequest, IntegrationConfig.IntegrationTopic, partitionId);
            Assert.IsTrue(r.Topics.First().Messages.FirstOrDefault().Value.ToUtf8String() == messge1);
        }
    }
}