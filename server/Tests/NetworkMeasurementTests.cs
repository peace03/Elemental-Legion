using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using ElementalLegion.Server;
using ElementalLegion.Shared.Net;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Sim;
using LiteNetLib;
using LiteNetLib.Utils;
using Xunit;
using Xunit.Abstractions;

namespace ElementalLegion.Tests;

/// <summary>실제 UDP 두 수신자로 같은 프로토콜·시야 복제·전송 통계의 시나리오 비용을 측정한다.</summary>
public sealed class NetworkMeasurementTests(ITestOutputHelper output)
{
    [Fact]
    public void LoadTraceFlushesConsecutiveAuthoritativeSamplesOnClose()
    {
        var directory = Directory.CreateTempSubdirectory("elemental-load-trace-");
        string path = Path.Combine(directory.FullName, "ticks.ndjson");
        try
        {
            var data = GameRulesTests.Data();
            var rules = new GameRules(data);
            var world = new NetworkWorld(rules, new NetworkSettings(data));
            Assert.Equal(JoinFailure.None, world.TryJoin(rules.StartingElements[0], out _));
            using (var trace = NetworkLoadTrace.Create(path, rules, "udp"))
            {
                for (int index = 0; index < 3; index++)
                {
                    long start = Stopwatch.GetTimestamp();
                    world.Step();
                    trace.Record(world, start);
                }
            }
            string[] lines = File.ReadAllLines(path);
            Assert.Equal(4, lines.Length);
            using var header = JsonDocument.Parse(lines[0]);
            Assert.Equal(rules.DataHash, header.RootElement.GetProperty("DataHash").GetString());
            for (int index = 1; index < lines.Length; index++)
            {
                using var sample = JsonDocument.Parse(lines[index]);
                Assert.Equal(index, sample.RootElement.GetProperty("Tick").GetInt64());
                Assert.Equal(1, sample.RootElement.GetProperty("Connected").GetInt32());
                Assert.Equal(rules.StartUnitCount, sample.RootElement.GetProperty("PlayerUnits").GetInt32());
            }
            Assert.Throws<IOException>(() => NetworkLoadTrace.Create(path, rules, "udp"));
        }
        finally
        {
            File.Delete(path);
            directory.Delete();
        }
    }

    [Fact, Trait("Category", "NetworkMeasurement")]
    public void ReportStationaryMovingEnterExitAndDenseObservation()
    {
        foreach (string scenario in new[] { "stationary", "moving", "enter-exit", "dense", "hidden-source" }) Measure(scenario);
    }

    private void Measure(string scenario)
    {
        var rules=GameRulesTests.Rules(); bool dense=scenario=="dense",hiddenSource=scenario=="hidden-source"; var setup=new WorldSetup();
        if(hiddenSource)
        {
            var json=GameRulesTests.Data().Root.DeepClone();int light=rules.ElementIndex("light");
            // 사망·전향으로 가시 공격자가 생기지 않게 하여 숨은 출처의 전송만 측정한다.
            json["elements"]![light]!["range"]=50;json["elements"]![light]!["attack"]=1;
            rules=new GameRules(new GameDataDocument(json.ToString()));
        }
        if(dense) setup=ScenarioFactory.Create(rules,rules.MaxPlayers,ScenarioKind.Clustered);
        else for(int owner=1;owner<=2;owner++)
        {
            Vector2 position=new(owner==1?100:scenario=="enter-exit"?160:hiddenSource?140:600,100);
            Vector2 home=hiddenSource&&owner==2?new Vector2(164,100):new Vector2(1000+owner*150);
            // 숨은 공격자 12기를 수비대로 두는 fixture는 해당 수비 상한의 레벨을 사용한다.
            setup.Players.Add(new PlayerSpawn(owner,"water",home,position,baseLevel:hiddenSource&&owner==2?2:1));
            for(int unit=0;unit<rules.StartUnitCount;unit++) setup.Units.Add(new UnitSpawn(owner,hiddenSource&&owner==2?"light":"water",position+new Vector2(0,unit*rules.SlotSpacing),garrison:hiddenSource&&owner==2));
        }
        var world=new SimulationWorld(rules,setup,1); var clock=Stopwatch.StartNew(); var peers=new Dictionary<int,NetPeer>();
        var senders=new[]{new FogReplicationState(rules),new FogReplicationState(rules)};
        var games=new[]{new GameplayReplicationState(rules),new GameplayReplicationState(rules)};var initialGame=new[]{true,true};
        var counters=new[]{new NetworkTrafficCounter(1),new NetworkTrafficCounter(2)};
        var listener=new EventBasedNetListener(); var server=new NetManager(listener){AutoRecycle=true,ChannelsCount=3,EnableStatistics=true};
        var writer=new NetDataWriter();
        int nextOwner=1;
        listener.ConnectionRequestEvent+=request=>
        {
            int helloBytes=request.Data.AvailableBytes;
            Assert.Equal(JoinFailure.None,Protocol.ReadHello(request.Data,rules,out _));
            int owner=nextOwner++;peers.Add(owner,request.Accept());counters[owner-1].RecordReceived(helloBytes,clock.Elapsed.TotalSeconds);
        };
        listener.PeerConnectedEvent+=peer=>
        {
            int owner=peers.First(pair=>pair.Value==peer).Key;Protocol.WriteWelcome(writer,owner,rules.MapSize);
            peer.Send(writer,DeliveryMethod.ReliableOrdered);counters[owner-1].RecordSent(writer.Length,PacketKind.Welcome,clock.Elapsed.TotalSeconds);
        };
        listener.NetworkReceiveEvent+=(peer,reader,channel,method)=>
        {
            int owner=peers.First(pair=>pair.Value==peer).Key; counters[owner-1].RecordReceived(reader.AvailableBytes,clock.Elapsed.TotalSeconds);
            ulong sequence=0;CommandAdmission admission=CommandAdmission.InvalidInput;
            if(channel==0&&method==DeliveryMethod.ReliableOrdered&&Protocol.TryReadCommand(reader,rules,out SimCommand command))
            {sequence=command.Sequence;admission=world.TryEnqueue(owner,command);}
            writer.Reset();writer.Put((byte)(admission==CommandAdmission.Accepted?PacketKind.Accepted:PacketKind.Rejected));writer.Put(sequence);
            if(admission!=CommandAdmission.Accepted)writer.Put((byte)admission);
            peer.Send(writer,DeliveryMethod.ReliableOrdered);counters[owner-1].RecordSent(writer.Length,(PacketKind)writer.Data[0],clock.Elapsed.TotalSeconds);
        };
        Assert.True(server.Start(0)); var clients=new NetManager[2]; var replica=new[]{new FogStateReplica(rules),new FogStateReplica(rules)};
        var chunks=new[]{new UnitSnapshotAssembler(rules),new UnitSnapshotAssembler(rules)};
        var gameplay=new[]{new GameplayReplica(rules),new GameplayReplica(rules)};
        int[] frames=new int[2],min={int.MaxValue,int.MaxValue},max=new int[2]; int forgot=0,redactedAttacks=0;
        try
        {
            for(int index=0;index<2;index++)
            {
                int current=index; var receive=new EventBasedNetListener(); clients[index]=new NetManager(receive){AutoRecycle=true,ChannelsCount=3};
                receive.NetworkReceiveEvent+=(_,reader,channel,_)=>
                {
                    if(channel==2)gameplay[current].TryAccept(reader,clock.Elapsed.TotalSeconds);
                    else if(channel==1){if(reader.PeekByte()==(byte)PacketKind.Forget)forgot++;else replica[current].TryAccept(reader);}
                    else if(chunks[current].TryAccept(reader)) { frames[current]++;min[current]=Math.Min(min[current],chunks[current].Count);max[current]=Math.Max(max[current],chunks[current].Count); }
                    while(gameplay[current].TryPublish(replica[current],out var completed))
                    {
                        if(hiddenSource&&current==0)foreach(var entry in completed.Events)if(entry.Data.Kind==SimEventKind.Attack)
                        {Assert.Equal(0,entry.Data.SourceId);Assert.Equal(-1,entry.Data.OwnerId);Assert.Equal(-1,entry.Data.Element);Assert.False(entry.Data.HasSourcePosition);Assert.Equal(Vector2.Zero,entry.Data.SourcePosition);redactedAttacks++;}
                    }
                };
                clients[index].Start();Protocol.WriteHello(writer,rules.DataHash,rules.StartingElements[0]);clients[index].Connect("127.0.0.1",server.LocalPort,writer);
            }
            double next=0;long tick=0;int duration=scenario=="enter-exit"?10:5;
            while(clock.Elapsed.TotalSeconds<duration)
            {
                server.PollEvents();foreach(NetManager client in clients)client.PollEvents();
                if(peers.Count==2 && clock.Elapsed.TotalSeconds>=next)
                {
                    next+=rules.DeltaTime; tick++;
                    if(scenario=="moving"||scenario=="enter-exit") for(int index=0;index<2;index++)
                    {
                        Vector2 direction=Vector2.UnitX*(index==0?1:-1)*(scenario=="enter-exit"&&clock.Elapsed.TotalSeconds>=5?-1:1);
                        Protocol.WriteMove(writer,(ulong)tick,direction);clients[index].FirstPeer.Send(writer,DeliveryMethod.ReliableOrdered);
                    }
                    world.Step();
                    for(int index=0;index<2;index++)
                    {
                        NetPeer peer=peers[index+1]; var view=world.GetView(index+1);
                        games[index].Capture(view);
                        void Send(byte channel,DeliveryMethod method)
                        {peer.Send(writer,channel,method);counters[index].RecordSent(writer.Length,(PacketKind)writer.Data[0],clock.Elapsed.TotalSeconds,channel==2&&initialGame[index]);}
                        if(tick%2==0)
                        {
                            var fog=senders[index]; fog.Prepare(view);
                            for(int part=0;part<fog.ForgetChunkCount;part++){fog.WriteForget(writer,part);Send(1,DeliveryMethod.ReliableOrdered);}
                            for(int part=0;part<fog.ChunkCount;part++){fog.WriteChunk(writer,part);Send(1,DeliveryMethod.ReliableOrdered);}
                            fog.Commit();
                            games[index].Prepare(view,fog.Revision);
                            for(int part=0;part<games[index].ChunkCount;part++){games[index].WriteChunk(writer,part);Send(2,DeliveryMethod.ReliableOrdered);}
                            games[index].Commit();initialGame[index]=false;
                            for(int part=0;part<Protocol.ChunkCount(view.Units.Count);part++){Protocol.WriteUnits(writer,view,rules.MapSize,part,fog.Revision);Send(0,DeliveryMethod.Unreliable);}
                        }
                        Protocol.WritePose(writer,view);Send(0,DeliveryMethod.Unreliable);
                        var stats=peer.Statistics;counters[index].SampleTransport(stats.BytesSent,stats.BytesReceived,stats.PacketsSent,stats.PacketsReceived,clock.Elapsed.TotalSeconds);
                    }
                }
                Thread.Sleep(1);
            }
            for(int index=0;index<2;index++)
            {
                Assert.True(replica[index].Ready);Assert.True(frames[index]>0);
                Assert.True(gameplay[index].State.Tick>=0);Assert.False(gameplay[index].Overflowed);Assert.True(counters[index].Snapshot(clock.Elapsed.TotalSeconds).SentPayload(PacketKind.Gameplay)>0);
                using var text=new StringWriter();counters[index].Snapshot(clock.Elapsed.TotalSeconds).Write(text);
                output.WriteLine($"{scenario}; observed min/max={min[index]}/{max[index]}; {text}");
            }
            if(scenario=="enter-exit") Assert.True(forgot>0,"실제 시야 진입 뒤 이탈 ID가 있어야 합니다.");
            if(hiddenSource){Assert.True(redactedAttacks>0,"숨겨진 공격자의 허용 피격 표현이 UDP에서 수신되어야 합니다.");output.WriteLine($"hidden-source redacted attacks={redactedAttacks}; fixture range50/attack1, gamedata source unchanged");}
        }
        finally {foreach(var game in gameplay)game.Dispose();foreach(NetManager? client in clients)client?.Stop();server.Stop();}
    }
}
