using System;
using System.IO;
using UnityEditor;

namespace ElementalLegion.Client.Editor
{
    /// <summary>캡처·렌더 표본을 차례로 실행해 서로 간의 부하 간섭을 피한다.</summary>
    public static class M3ProbeSuite
    {
        private static readonly string[] Names={"normal-m2-markers","normal-m3","dense-m2-markers","dense-m3","combat-m3","gallery-final","combat-final"};
        private static int index=-1;
        private static bool started;
        public static void Start()
        {
            if(index>=0)throw new InvalidOperationException("M3 표본 모음이 이미 실행 중입니다.");
            index=0;started=false;EditorApplication.update+=Update;
        }
        private static void Update()
        {
            if(M3RenderProbe.Running)return;
            if(started){index++;started=false;}
            if(index>=Names.Length){EditorApplication.update-=Update;index=-1;File.WriteAllText("Temp/M3Work/probe-suite-status.txt","complete");return;}
            string name=Names[index];File.WriteAllText("Temp/M3Work/probe-suite-status.txt",name);
            M3RenderProbe.Start(index==2||index==3?988:index==4||index==6?28:14,index!=0&&index!=2,"Temp/M3Work/"+name+".json",index==4||index==6,index>=5,index==0||index==2);started=true;
        }
    }
}
