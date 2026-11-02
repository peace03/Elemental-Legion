using System;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Net;
using LiteNetLib.Utils;
using UnityEngine;

namespace ElementalLegion.Client.FogOfWar
{
    /// <summary>서버와 분리된 효과 비교 씬 전용 마스크다. 게임 씬에서는 사용하지 않는다.</summary>
    public sealed class GalleryFog : MonoBehaviour
    {
        [SerializeField,Tooltip("공유 JSON 원본입니다.")] private TextAsset gameData;
        [SerializeField,Tooltip("갤러리 카메라의 연무입니다.")] private FogPresentation fog;
        public void Initialize()
        {
            var rules=new GameRules(new GameDataDocument(gameData.text));fog.Initialize(rules);
            var replica=new FogStateReplica(rules);int count=(replica.CellCount+7)/8;byte[] body=new byte[count*2];
            for(int cell=0;cell<replica.CellCount;cell++){body[cell>>3]|=(byte)(1<<(cell&7));body[count+(cell>>3)]|=(byte)(1<<(cell&7));}
            var writer=new NetDataWriter();
            for(int offset=0;offset<body.Length;offset+=FogProtocol.PayloadBytes)
            {writer.Reset();writer.Put((byte)PacketKind.FogBaseline);writer.Put(0L);writer.Put(1);writer.Put(1U);writer.Put(0U);writer.Put(body.Length);writer.Put(offset);writer.Put(body,offset,Math.Min(FogProtocol.PayloadBytes,body.Length-offset));replica.TryAccept(new NetDataReader(writer.CopyData()));}
            fog.Apply(replica);
        }
        private void OnEnable(){if(gameData!=null&&fog!=null)Initialize();}
    }
}
