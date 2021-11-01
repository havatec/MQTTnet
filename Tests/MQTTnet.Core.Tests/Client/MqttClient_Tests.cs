using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MQTTnet.Client;
using MQTTnet.Exceptions;
using MQTTnet.Formatter;
using MQTTnet.Packets;
using MQTTnet.Protocol;
using MQTTnet.Tests.Mockups;

namespace MQTTnet.Tests.Client
{
    [TestClass]
    public class Client_Tests : BaseTestClass
    {
        [TestMethod]
        public async Task Ensure_Queue_Drain()
        {
            using (var testEnvironment = new TestEnvironment(TestContext))
            {
                var server = await testEnvironment.StartServer();
                var client = await testEnvironment.ConnectLowLevelClient();

                var i = 0;
                server.InterceptingPublishAsync += c =>
                {
                    i++;
                    return Task.CompletedTask;
                };

                await client.SendAsync(new MqttConnectPacket
                {
                    ClientId = "Ensure_Queue_Drain_Test"
                }, CancellationToken.None);

                await client.SendAsync(new MqttPublishPacket
                {
                    Topic = "Test"
                }, CancellationToken.None);
                
                await Task.Delay(500);
                
                // This will simulate a device which closes the connection directly
                // after sending the data so do delay is added between send and dispose!
                client.Dispose();
                
                await Task.Delay(1000);

                Assert.AreEqual(1, i);
            }
        }
        
        [TestMethod]
        public async Task Set_ClientWasConnected_On_ServerDisconnect()
        {
            using (var testEnvironment = new TestEnvironment(TestContext))
            {
                var server = await testEnvironment.StartServer();
                var client = await testEnvironment.ConnectClient();

                Assert.IsTrue(client.IsConnected);
                client.UseDisconnectedHandler(e => Assert.IsTrue(e.ClientWasConnected));

                await server.StopAsync();
                await Task.Delay(4000);
            }
        }

        [TestMethod]
        public async Task Set_ClientWasConnected_On_ClientDisconnect()
        {
            using (var testEnvironment = new TestEnvironment(TestContext))
            {
                var server = await testEnvironment.StartServer();
                var client = await testEnvironment.ConnectClient();

                Assert.IsTrue(client.IsConnected);
                client.UseDisconnectedHandler(e => Assert.IsTrue(e.ClientWasConnected));

                await client.DisconnectAsync();
                await Task.Delay(200);
            }
        }

        [TestMethod]
        [ExpectedException(typeof(MqttCommunicationTimedOutException))]
        public async Task Connect_To_Invalid_Server_Wrong_IP()
        {
            var client = new MqttFactory().CreateMqttClient();
            await client.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer("1.2.3.4").WithCommunicationTimeout(TimeSpan.FromSeconds(2)).Build());
        }

        [TestMethod]
        [ExpectedException(typeof(MqttCommunicationException))]
        public async Task Connect_To_Invalid_Server_Port_Not_Opened()
        {
            var client = new MqttFactory().CreateMqttClient();
            await client.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer("127.0.0.1", 12345).WithCommunicationTimeout(TimeSpan.FromSeconds(5)).Build());
        }

        [TestMethod]
        [ExpectedException(typeof(MqttCommunicationException))]
        public async Task Connect_To_Invalid_Server_Wrong_Protocol()
        {
            var client = new MqttFactory().CreateMqttClient();
            await client.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer("http://127.0.0.1", 12345).WithCommunicationTimeout(TimeSpan.FromSeconds(2)).Build());
        }

        [TestMethod]
        public async Task Send_Manual_Ping()
        {
            using (var testEnvironment = new TestEnvironment(TestContext))
            {
                await testEnvironment.StartServer();
                var client = await testEnvironment.ConnectClient();

                await client.PingAsync(CancellationToken.None);
            }
        }

        [TestMethod]
        public async Task Send_Reply_In_Message_Handler_For_Same_Client()
        {
            using (var testEnvironment = new TestEnvironment(TestContext))
            {
                await testEnvironment.StartServer();
                var client = await testEnvironment.ConnectClient();

                await client.SubscribeAsync("#");

                var replyReceived = false;

                client.ApplicationMessageReceivedAsync += e =>
                {
                    if (e.ApplicationMessage.Topic == "request")
                    {
#pragma warning disable 4014
                        Task.Run(() => client.PublishAsync("reply", null, MqttQualityOfServiceLevel.AtLeastOnce));
#pragma warning restore 4014
                    }
                    else
                    {
                        replyReceived = true;
                    }
                    
                    return Task.CompletedTask;
                };

                await client.PublishAsync("request", null, MqttQualityOfServiceLevel.AtLeastOnce);

                SpinWait.SpinUntil(() => replyReceived, TimeSpan.FromSeconds(10));

                Assert.IsTrue(replyReceived);
            }
        }

        [TestMethod]
        public async Task Send_Reply_In_Message_Handler()
        {
            using (var testEnvironment = new TestEnvironment())
            {
                await testEnvironment.StartServer();
                var client1 = await testEnvironment.ConnectClient();
                var client2 = await testEnvironment.ConnectClient();

                await client1.SubscribeAsync("#");
                await client2.SubscribeAsync("#");

                var replyReceived = false;

                client1.ApplicationMessageReceivedAsync += e =>
                {
                    if (e.ApplicationMessage.Topic == "reply")
                    {
                        replyReceived = true;
                    }
                    
                    return Task.CompletedTask;
                };

                client2.ApplicationMessageReceivedAsync += async e =>
                {
                    if (e.ApplicationMessage.Topic == "request")
                    {
                        // Use AtMostOnce here because with QoS 1 or even QoS 2 the process waits for 
                        // the ACK etc. The problem is that the SpinUntil below only waits until the 
                        // flag is set. It does not wait until the client has sent the ACK
                        await client2.PublishAsync("reply", null, MqttQualityOfServiceLevel.AtMostOnce);
                    }
                };

                await client1.PublishAsync("request", null, MqttQualityOfServiceLevel.AtLeastOnce);

                await Task.Delay(500);

                SpinWait.SpinUntil(() => replyReceived, TimeSpan.FromSeconds(10));

                await Task.Delay(500);

                Assert.IsTrue(replyReceived);
            }
        }

        [TestMethod]
        public async Task Reconnect()
        {
            using (var testEnvironment = new TestEnvironment(TestContext))
            {
                var server = await testEnvironment.StartServer();
                var client = await testEnvironment.ConnectClient();

                await Task.Delay(500);
                Assert.IsTrue(client.IsConnected);

                await server.StopAsync();
                await Task.Delay(500);
                Assert.IsFalse(client.IsConnected);

                await server.StartAsync();
                await Task.Delay(500);

                await client.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer("127.0.0.1", testEnvironment.ServerPort).Build());
                Assert.IsTrue(client.IsConnected);
            }
        }

        [TestMethod]
        public async Task Reconnect_While_Server_Offline()
        {
            using (var testEnvironment = new TestEnvironment(TestContext))
            {
                testEnvironment.IgnoreClientLogErrors = true;

                var server = await testEnvironment.StartServer();
                var client = await testEnvironment.ConnectClient();

                await Task.Delay(500);
                Assert.IsTrue(client.IsConnected);

                await server.StopAsync();
                await Task.Delay(500);
                Assert.IsFalse(client.IsConnected);

                for (var i = 0; i < 5; i++)
                {
                    try
                    {
                        await client.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer("127.0.0.1", testEnvironment.ServerPort).Build());
                        Assert.Fail("Must fail!");
                    }
                    catch
                    {
                    }
                }

                await server.StartAsync();
                await Task.Delay(500);

                await client.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer("127.0.0.1", testEnvironment.ServerPort).Build());
                Assert.IsTrue(client.IsConnected);
            }
        }

        [TestMethod]
        public async Task Reconnect_From_Disconnected_Event()
        {
            using (var testEnvironment = new TestEnvironment(TestContext))
            {
                testEnvironment.IgnoreClientLogErrors = true;

                var client = testEnvironment.CreateClient();

                var tries = 0;
                var maxTries = 3;

                client.UseDisconnectedHandler(async e =>
                {
                    if (tries >= maxTries)
                    {
                        return;
                    }

                    Interlocked.Increment(ref tries);

                    await Task.Delay(100);
                    await client.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer("127.0.0.1", testEnvironment.ServerPort).Build());
                });

                try
                {
                    await client.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer("127.0.0.1", testEnvironment.ServerPort).Build());
                    Assert.Fail("Must fail!");
                }
                catch
                {
                }

                SpinWait.SpinUntil(() => tries >= maxTries, 10000);

                Assert.AreEqual(maxTries, tries);
            }
        }

        [TestMethod]
        public async Task PacketIdentifier_In_Publish_Result()
        {
            using (var testEnvironment = new TestEnvironment(TestContext))
            {
                await testEnvironment.StartServer();
                var client = await testEnvironment.ConnectClient();

                var result = await client.PublishAsync("a", "a", MqttQualityOfServiceLevel.AtMostOnce);
                Assert.AreEqual(null, result.PacketIdentifier);

                result = await client.PublishAsync("b", "b", MqttQualityOfServiceLevel.AtMostOnce);
                Assert.AreEqual(null, result.PacketIdentifier);

                result = await client.PublishAsync("a", "a", MqttQualityOfServiceLevel.AtLeastOnce);
                Assert.AreEqual((ushort)1, result.PacketIdentifier);

                result = await client.PublishAsync("b", "b", MqttQualityOfServiceLevel.AtLeastOnce);
                Assert.AreEqual((ushort)2, result.PacketIdentifier);

                result = await client.PublishAsync("a", "a", MqttQualityOfServiceLevel.ExactlyOnce);
                Assert.AreEqual((ushort)3, result.PacketIdentifier);

                result = await client.PublishAsync("b", "b", MqttQualityOfServiceLevel.ExactlyOnce);
                Assert.AreEqual((ushort)4, result.PacketIdentifier);
            }
        }

        [TestMethod]
        public async Task Invalid_Connect_Throws_Exception()
        {
            var factory = new MqttFactory();
            using (var client = factory.CreateMqttClient())
            {
                try
                {
                    await client.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer("wrong-server").Build());

                    Assert.Fail("Must fail!");
                }
                catch (Exception exception)
                {
                    Assert.IsNotNull(exception);
                    Assert.IsInstanceOfType(exception, typeof(MqttCommunicationException));
                    Assert.IsInstanceOfType(exception.InnerException, typeof(SocketException));
                }
            }
        }

        [TestMethod]
        public async Task ConnectTimeout_Throws_Exception()
        {
            var factory = new MqttFactory();
            using (var client = factory.CreateMqttClient())
            {
                bool disconnectHandlerCalled = false;
                try
                {
                    client.DisconnectedHandler = new MqttClientDisconnectedHandlerDelegate(args =>
                    {
                        disconnectHandlerCalled = true;
                    });

                    await client.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer("1.2.3.4").Build());

                    Assert.Fail("Must fail!");
                }
                catch (Exception exception)
                {
                    Assert.IsNotNull(exception);
                    Assert.IsInstanceOfType(exception, typeof(MqttCommunicationException));
                    //Assert.IsInstanceOfType(exception.InnerException, typeof(SocketException));
                }

                await Task.Delay(100); // disconnected handler is called async
                Assert.IsTrue(disconnectHandlerCalled);
            }
        }

        [TestMethod]
        public async Task Fire_Disconnected_Event_On_Server_Shutdown()
        {
            using (var testEnvironment = new TestEnvironment(TestContext))
            {
                var server = await testEnvironment.StartServer();
                var client = await testEnvironment.ConnectClient();

                var handlerFired = false;
                client.UseDisconnectedHandler(e => handlerFired = true);

                await server.StopAsync();

                await Task.Delay(4000);

                Assert.IsTrue(handlerFired);
            }
        }

        [TestMethod]
        public async Task Disconnect_Event_Contains_Exception()
        {
            var factory = new MqttFactory();
            using (var client = factory.CreateMqttClient())
            {
                Exception ex = null;
                client.DisconnectedHandler = new MqttClientDisconnectedHandlerDelegate(e =>
                {
                    ex = e.Exception;
                });

                try
                {
                    await client.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer("wrong-server").Build());
                }
                catch
                {
                }

                await Task.Delay(500);

                Assert.IsNotNull(ex);
                Assert.IsInstanceOfType(ex, typeof(MqttCommunicationException));
                Assert.IsInstanceOfType(ex.InnerException, typeof(SocketException));
            }
        }

        [TestMethod]
        public async Task Preserve_Message_Order()
        {
            // The messages are sent in reverse or to ensure that the delay in the handler
            // needs longer for the first messages and later messages may be processed earlier (if there
            // is an issue).
            const int MessagesCount = 50;

            using (var testEnvironment = new TestEnvironment(TestContext))
            {
                await testEnvironment.StartServer();

                var client1 = await testEnvironment.ConnectClient();
                await client1.SubscribeAsync("x");

                var receivedValues = new List<int>();

                async Task Handler1(MqttApplicationMessageReceivedEventArgs eventArgs)
                {
                    var value = int.Parse(eventArgs.ApplicationMessage.ConvertPayloadToString());
                    await Task.Delay(value);

                    lock (receivedValues)
                    {
                        receivedValues.Add(value);
                    }
                }

                client1.ApplicationMessageReceivedAsync += Handler1;

                var client2 = await testEnvironment.ConnectClient();
                for (var i = MessagesCount; i > 0; i--)
                {
                    await client2.PublishAsync("x", i.ToString());
                }

                await Task.Delay(5000);

                for (var i = MessagesCount; i > 0; i--)
                {
                    Assert.AreEqual(i, receivedValues[MessagesCount - i]);
                }
            }
        }

        [TestMethod]
        public async Task Preserve_Message_Order_With_Delayed_Acknowledgement()
        {
            // The messages are sent in reverse or to ensure that the delay in the handler
            // needs longer for the first messages and later messages may be processed earlier (if there
            // is an issue).
            const int MessagesCount = 50;

            using (var testEnvironment = new TestEnvironment(TestContext))
            {
                await testEnvironment.StartServer();

                var client1 = await testEnvironment.ConnectClient();
                await client1.SubscribeAsync("x", MqttQualityOfServiceLevel.ExactlyOnce);

                var receivedValues = new List<int>();

                Task Handler1(MqttApplicationMessageReceivedEventArgs eventArgs)
                {
                    var value = int.Parse(eventArgs.ApplicationMessage.ConvertPayloadToString());
                    eventArgs.AutoAcknowledge = false;
                    Task.Delay(value).ContinueWith(x => eventArgs.AcknowledgeAsync(CancellationToken.None));

                    System.Diagnostics.Debug.WriteLine($"received {value}");
                    lock (receivedValues)
                    {
                        receivedValues.Add(value);
                    }

                    return Task.CompletedTask;
                }

                client1.ApplicationMessageReceivedAsync += Handler1;

                var client2 = await testEnvironment.ConnectClient();
                for (var i = MessagesCount; i > 0; i--)
                {
                    await client2.PublishAsync("x", i.ToString(), MqttQualityOfServiceLevel.ExactlyOnce);
                }

                await Task.Delay(5000);

                for (var i = MessagesCount; i > 0; i--)
                {
                    Assert.AreEqual(i, receivedValues[MessagesCount - i]);
                }
            }
        }

        [TestMethod]
        public async Task Send_Reply_For_Any_Received_Message()
        {
            using (var testEnvironment = new TestEnvironment(TestContext))
            {
                await testEnvironment.StartServer();

                var client1 = await testEnvironment.ConnectClient();
                await client1.SubscribeAsync("request/+");

                async Task Handler1(MqttApplicationMessageReceivedEventArgs eventArgs)
                {
                    await client1.PublishAsync($"reply/{eventArgs.ApplicationMessage.Topic}");
                }

                client1.ApplicationMessageReceivedAsync += Handler1;

                var client2 = await testEnvironment.ConnectClient();
                await client2.SubscribeAsync("reply/#");

                var replies = new List<string>();

                Task Handler2(MqttApplicationMessageReceivedEventArgs eventArgs)
                {
                    lock (replies)
                    {
                        replies.Add(eventArgs.ApplicationMessage.Topic);
                    }

                    return Task.CompletedTask;
                }

                client2.ApplicationMessageReceivedAsync += Handler2;

                await Task.Delay(500);

                await client2.PublishAsync("request/a");
                await client2.PublishAsync("request/b");
                await client2.PublishAsync("request/c");

                await Task.Delay(500);

                Assert.AreEqual("reply/request/a,reply/request/b,reply/request/c", string.Join(",", replies));
            }
        }

        [TestMethod]
        public async Task Publish_With_Correct_Retain_Flag()
        {
            using (var testEnvironment = new TestEnvironment(TestContext))
            {
                await testEnvironment.StartServer();

                var receivedMessages = new List<MqttApplicationMessage>();

                var client1 = await testEnvironment.ConnectClient();
                client1.ApplicationMessageReceivedAsync += e =>
                {
                    lock (receivedMessages)
                    {
                        receivedMessages.Add(e.ApplicationMessage);
                    }
                    
                    return Task.CompletedTask;
                };

                await client1.SubscribeAsync("a");

                var client2 = await testEnvironment.ConnectClient();
                var message = new MqttApplicationMessageBuilder().WithTopic("a").WithRetainFlag().Build();
                await client2.PublishAsync(message);

                await Task.Delay(500);

                Assert.AreEqual(1, receivedMessages.Count);
                Assert.IsFalse(receivedMessages.First().Retain); // Must be false even if set above!
            }
        }

        [TestMethod]
        public async Task Publish_QoS_1_In_ApplicationMessageReceiveHandler()
        {
            using (var testEnvironment = new TestEnvironment(TestContext))
            {
                await testEnvironment.StartServer();

                const string client1Topic = "client1/topic";
                const string client2Topic = "client2/topic";
                const string expectedClient2Message = "hello client2";

                var client1 = await testEnvironment.ConnectClient();
                client1.ApplicationMessageReceivedAsync += async e =>
                {
                    await client1.PublishAsync(client2Topic, expectedClient2Message, MqttQualityOfServiceLevel.AtLeastOnce);
                };

                await client1.SubscribeAsync(client1Topic, MqttQualityOfServiceLevel.AtLeastOnce);

                var client2 = await testEnvironment.ConnectClient();

                var client2TopicResults = new List<string>();

                client2.ApplicationMessageReceivedAsync += e =>
                {
                    client2TopicResults.Add(Encoding.UTF8.GetString(e.ApplicationMessage.Payload));
                    return Task.CompletedTask;
                };

                await client2.SubscribeAsync(client2Topic);

                var client3 = await testEnvironment.ConnectClient();
                var message = new MqttApplicationMessageBuilder().WithTopic(client1Topic).Build();
                await client3.PublishAsync(message);
                await client3.PublishAsync(message);

                await Task.Delay(500);

                Assert.AreEqual(2, client2TopicResults.Count);
                Assert.AreEqual(expectedClient2Message, client2TopicResults[0]);
                Assert.AreEqual(expectedClient2Message, client2TopicResults[1]);
            }
        }

        [TestMethod]
        public async Task Publish_QoS_0_Over_Period_Exceeding_KeepAlive()
        {
            using (var testEnvironment = new TestEnvironment(TestContext))
            {
                const int KeepAlivePeriodSecs = 3;

                await testEnvironment.StartServer();

                var options = new MqttClientOptionsBuilder().WithKeepAlivePeriod(TimeSpan.FromSeconds(KeepAlivePeriodSecs));
                var client = await testEnvironment.ConnectClient(options);
                var message = new MqttApplicationMessageBuilder().WithTopic("a").Build();

                try
                {
                    // Publish messages over a time period exceeding the keep alive period.
                    // This should not cause an exception because of, i.e. "Client disconnected".

                    for (var count = 0; count < KeepAlivePeriodSecs * 3; ++count)
                    {
                        // Send Publish requests well before the keep alive period expires
                        await client.PublishAsync(message);
                        await Task.Delay(1000);
                    }
                }
                catch (Exception ex)
                {
                    Assert.Fail(ex.Message);
                }
            }
        }


        [TestMethod]
        public async Task Subscribe_In_Callback_Events()
        {
            using (var testEnvironment = new TestEnvironment(TestContext))
            {
                await testEnvironment.StartServer();

                var receivedMessages = new List<MqttApplicationMessage>();

                var client = testEnvironment.CreateClient();

                client.ConnectedHandler = new MqttClientConnectedHandlerDelegate(async e =>
                {
                    await client.SubscribeAsync("RCU/P1/H0001/R0003");

                    var msg = new MqttApplicationMessageBuilder()
                        .WithPayload("DA|18RS00SC00XI0000RV00R100R200R300R400L100L200L300L400Y100Y200AC0102031800BELK0000BM0000|")
                        .WithTopic("RCU/P1/H0001/R0003");

                    await client.PublishAsync(msg.Build());
                });

                client.ApplicationMessageReceivedAsync += e =>
                {
                    lock (receivedMessages)
                    {
                        receivedMessages.Add(e.ApplicationMessage);
                    }
                    
                    return Task.CompletedTask;
                };

                await client.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer("localhost", testEnvironment.ServerPort).Build());

                await Task.Delay(500);

                Assert.AreEqual(1, receivedMessages.Count);
                Assert.AreEqual("DA|18RS00SC00XI0000RV00R100R200R300R400L100L200L300L400Y100Y200AC0102031800BELK0000BM0000|", receivedMessages.First().ConvertPayloadToString());
            }
        }
        
        [TestMethod]
        public async Task NoConnectedHandler_Connect_DoesNotThrowException()
        {
            using (var testEnvironment = new TestEnvironment(TestContext))
            {
                await testEnvironment.StartServer();

                var client = await testEnvironment.ConnectClient();

                Assert.IsTrue(client.IsConnected);
            }
        }

        [TestMethod]
        public async Task NoDisconnectedHandler_Disconnect_DoesNotThrowException()
        {
            using (var testEnvironment = new TestEnvironment(TestContext))
            {
                await testEnvironment.StartServer();
                var client = await testEnvironment.ConnectClient();
                Assert.IsTrue(client.IsConnected);

                await client.DisconnectAsync();

                Assert.IsFalse(client.IsConnected);
            }
        }

        [TestMethod]
        public async Task Frequent_Connects()
        {
            using (var testEnvironment = new TestEnvironment(TestContext))
            {
                await testEnvironment.StartServer();

                var clients = new List<IMqttClient>();
                for (var i = 0; i < 100; i++)
                {
                    clients.Add(await testEnvironment.ConnectClient(new MqttClientOptionsBuilder().WithClientId("a")));
                }

                await Task.Delay(500);

                var clientStatus = await testEnvironment.Server.GetClientsAsync();
                var sessionStatus = await testEnvironment.Server.GetSessionsAsync();

                for (var i = 0; i < 98; i++)
                {
                    Assert.IsFalse(clients[i].IsConnected, $"clients[{i}] is not connected");
                }

                Assert.IsTrue(clients[99].IsConnected);

                Assert.AreEqual(1, clientStatus.Count);
                Assert.AreEqual(1, sessionStatus.Count);

                var receiveClient = clients[99];
                object receivedPayload = null;
                receiveClient.ApplicationMessageReceivedAsync += e =>
                {
                    receivedPayload = e.ApplicationMessage.ConvertPayloadToString();
                    return Task.CompletedTask;
                };

                await receiveClient.SubscribeAsync("x");

                var sendClient = await testEnvironment.ConnectClient();
                await sendClient.PublishAsync("x", "1");

                await Task.Delay(250);

                Assert.AreEqual("1", receivedPayload);
            }
        }

        [TestMethod]
        public async Task No_Payload()
        {
            using (var testEnvironment = new TestEnvironment(TestContext))
            {
                await testEnvironment.StartServer();

                var sender = await testEnvironment.ConnectClient();
                var receiver = await testEnvironment.ConnectClient();

                var message = new MqttApplicationMessageBuilder()
                    .WithTopic("A");

                await receiver.SubscribeAsync(new MqttClientSubscribeOptions
                {
                    TopicFilters = new List<MqttTopicFilter> { new MqttTopicFilter { Topic = "#" } }
                }, CancellationToken.None);

                MqttApplicationMessage receivedMessage = null;
                receiver.ApplicationMessageReceivedAsync += e =>
                {
                    receivedMessage = e.ApplicationMessage;
                    return Task.CompletedTask;
                };

                await sender.PublishAsync(message.Build(), CancellationToken.None);

                await Task.Delay(1000);

                Assert.IsNotNull(receivedMessage);
                Assert.AreEqual("A", receivedMessage.Topic);
                Assert.AreEqual(null, receivedMessage.Payload);
            }
        }

        [TestMethod]
        public async Task Subscribe_With_QoS2()
        {
            using (var testEnvironment = new TestEnvironment())
            {
                await testEnvironment.StartServer();
                var client1 = await testEnvironment.ConnectClient(o => o.WithProtocolVersion(MqttProtocolVersion.V500));
                var client2 = await testEnvironment.ConnectClient(o => o.WithProtocolVersion(MqttProtocolVersion.V500));

                var disconnectedFired = false;
                client1.DisconnectedHandler = new MqttClientDisconnectedHandlerDelegate(c =>
                {
                    disconnectedFired = true;
                });

                var messageReceived = false;
                client1.ApplicationMessageReceivedAsync += e =>
                {
                    messageReceived = true;
                    return Task.CompletedTask;
                };

                await client1.SubscribeAsync(new MqttTopicFilterBuilder().WithTopic("topic1").WithExactlyOnceQoS().Build());

                await Task.Delay(500);

                var message = new MqttApplicationMessageBuilder().WithTopic("topic1").WithPayload("Hello World").WithExactlyOnceQoS().WithRetainFlag().Build();
                
                await client2.PublishAsync(message);
                await Task.Delay(500);

                Assert.IsTrue(messageReceived);
                Assert.IsTrue(client1.IsConnected);
                Assert.IsFalse(disconnectedFired);
            }
        }
        
        [DataTestMethod]
        [DataRow(MqttQualityOfServiceLevel.ExactlyOnce)]
        [DataRow(MqttQualityOfServiceLevel.AtMostOnce)]
        [DataRow(MqttQualityOfServiceLevel.AtLeastOnce)]
        public async Task Concurrent_Processing(MqttQualityOfServiceLevel qos)
        {
            long concurrency = 0;
            bool success = false;

            using (var testEnvironment = new TestEnvironment(TestContext))
            {
                await testEnvironment.StartServer();
                var publisher = await testEnvironment.ConnectClient();
                var subscriber = await testEnvironment.ConnectClient(new MqttClientOptionsBuilder().WithClientId(qos.ToString()));
                await subscriber.SubscribeAsync("#", qos);

                subscriber.ApplicationMessageReceivedAsync += e =>
                {
                    e.AutoAcknowledge = false;

                    async Task InvokeInternal()
                    {
                        if (Interlocked.Increment(ref concurrency) > 1) success = true;
                        await Task.Delay(100);
                        Interlocked.Decrement(ref concurrency);
                    }

                    _ = InvokeInternal();
                    return Task.CompletedTask;
                };

                var publishes = Task.WhenAll(
                    publisher.PublishAsync("a", null, qos),
                    publisher.PublishAsync("b", null, qos)
                );

                await Task.Delay(200);

                await publishes;
                Assert.IsTrue(success);
            }
        }
    }
}
