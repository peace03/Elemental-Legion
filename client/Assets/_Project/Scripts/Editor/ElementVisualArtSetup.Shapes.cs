using System;
using UnityEngine;

namespace ElementalLegion.Client.Editor
{
    public static partial class ElementVisualArtSetup
    {
        // r=물질 구분, g=부품 위상, b=.5는 빛의 가장자리를 흐리게 처리할 열린 흐름이다.
        private static Mesh BuildMesh(string id, int style)
        {
            var shape = new Shape();
            switch (style)
            {
                case 0:
                    shape.WaterMass();
                    shape.WaterSheet(1);
                    shape.WaterSheet(-1);
                    for(int j=0;j<9;j++)shape.SmallDrop(Orbit(j*2.399f,.35f+(j%3)*.036f,-.24f+(j%5)*.15f),.026f+(j%3)*.009f,.09f+(j%2)*.065f,.8f);
                    break;
                case 1:
                    for(int j=0;j<5;j++)
                    {
                        int part=j;
                        shape.Tube(t=>new Vector3(Mathf.Sin(t*7+part*1.7f)*(.08f+t*.13f),-.42f+t*(part==0?1.32f:1.04f),Mathf.Cos(t*5+part*2)*.16f),
                            t=>Mathf.Pow(1-t,1.15f)*(.12f+part*.019f)+.003f,16,6,1,.72f);
                    }
                    break;
                case 2:
                    for(int j=0;j<3;j++)
                    {
                        int part=j;
                        shape.Ribbon(t=>Orbit(t*5.4f+part*2.399f,(.11f+t*.36f)*(1+.12f*Mathf.Sin(t*9+part)),
                            -.33f+t*.95f+Mathf.Sin(t*6+part)*.09f),t=>.007f+Mathf.Sin(t*Mathf.PI)*.068f,30,1,j*.23f);
                    }
                    for(int j=0;j<6;j++)shape.Rock(Orbit(j*2.4f,.46f,Mathf.Sin(j)*.22f),Vector3.one*(.028f+j*.003f),j,0,5,3);
                    break;
                case 3:
                    for(int j=0;j<5;j++)shape.Liquid(Orbit(j*2.3f,.11f,-.29f+j*.18f),new Vector3(.33f,.29f,.31f),j);
                    break;
                case 4:
                    shape.Shard(new Vector3(-.04f,.01f,0),new Vector3(.37f,.74f,.56f),.28f,1.02f,.08f,.78f);
                    shape.Shard(new Vector3(.13f,-.06f,-.1f),new Vector3(.28f,.7f,.64f),.23f,.82f,.19f,.69f);
                    shape.Shard(new Vector3(-.16f,-.13f,.12f),new Vector3(.62f,.45f,.22f),.19f,.68f,.26f,.65f);
                    for(int j=0;j<7;j++)shape.Shard(Orbit(j*2.4f,.46f,-.34f+j*.11f),Orbit(j,.48f,1),.024f,.12f+(j%2)*.08f,.8f,.52f);
                    break;
                case 5:
                    shape.Rock(new Vector3(-.12f,-.03f,0),new Vector3(.33f,.36f,.31f),.8f,0,8,5);
                    shape.Rock(new Vector3(.23f,-.04f,.03f),new Vector3(.23f,.28f,.23f),1.24f,0,8,4);
                    shape.Rock(new Vector3(-.04f,.29f,-.07f),new Vector3(.22f,.22f,.20f),2.17f,0,7,4);
                    for(int j=0;j<7;j++)shape.Rock(Orbit(j*2.399f,.35f+(j%2)*.1f,-.3f+(j%4)*.2f),new Vector3(.065f+(j%3)*.02f,.075f+(j%2)*.043f,.06f),j+.22f,.25f,6,3);
                    break;
                case 6:
                    shape.Liquid(Vector3.zero,new Vector3(.065f,.10f,.065f),.3f);
                    for(int j=0;j<9;j++)
                    {
                        int branch=j;
                        shape.Tube(t=>LightningFork(t,branch),t=>(branch==0?.044f:.018f+.004f*(branch%3))*(1-t*.68f),18,6,1);
                    }
                    break;
                case 7:
                    shape.Liquid(Vector3.zero,Vector3.one*.17f,0);
                    for(int j=0;j<4;j++)
                    {
                        int part=j;
                        Quaternion tilt=Quaternion.Euler(j*38+14,j*51,j*28+8);
                        shape.Ribbon(t=>tilt*Orbit(t*(3.7f+part*.14f)+part,.037f+Mathf.Pow(t,.85f)*(.32f+part*.016f),
                            Mathf.Sin(t*5.5f+part)*.093f),t=>.008f+.036f*Mathf.Sin(t*Mathf.PI),30,1,j*.21f);
                    }
                    for(int j=0;j<6;j++)
                    {
                        Vector3 ray=Orbit(j*2.399f,.44f,Mathf.Sin(j*1.4f)*.24f);
                        shape.Tube(t=>ray*t,t=>.018f*Mathf.Pow(1-t,2)+.001f,8,4,1,.5f);
                    }
                    break;
                case 8:
                    shape.Rock(new Vector3(0,-.24f,0),new Vector3(.34f,.25f,.3f),2,0,9,4);
                    for(int j=0;j<9;j++)shape.Shard(Orbit(j*2.399f,j==0?0:.23f,-.22f+(j%3)*.09f),
                        Orbit(j,.32f,1),.095f+(j%3)*.032f,j==0?1.1f:.43f+(j%3)*.18f,.65f,1,6);
                    for(int j=0;j<5;j++)shape.Shard(Orbit(j*2.4f,.48f,-.25f+j*.12f),Orbit(j,.4f,1),.022f,.15f,.8f,1,6);
                    break;
                case 9:
                    shape.Rock(new Vector3(-.11f,-.12f,.01f),new Vector3(.30f,.32f,.27f),.42f,0,10,5);
                    shape.Rock(new Vector3(.13f,.09f,.04f),new Vector3(.27f,.29f,.24f),1.14f,0,10,5);
                    shape.Rock(new Vector3(-.08f,.25f,-.09f),new Vector3(.18f,.19f,.17f),2.31f,0,9,4);
                    shape.Rock(new Vector3(.13f,-.24f,-.1f),new Vector3(.15f,.14f,.15f),3.1f,0,8,4);
                    for(int j=0;j<6;j++)shape.Rock(Orbit(j*2.399f,.40f+(j%2)*.035f,-.29f+j*.11f),Vector3.one*(.032f+(j%3)*.016f),j+.17f,.6f,6,3);
                    break;
                case 10:
                    shape.Liquid(Vector3.zero,Vector3.one*.23f,0);
                    Quaternion disk=Quaternion.Euler(24,0,13);
                    for(int j=0;j<2;j++)
                    {
                        int arm=j;
                        shape.Ribbon(t=>disk*Orbit(t*(arm==0?6.0f:3.3f)+arm*2.8f,.105f+t*.37f+
                            Mathf.Sin(t*13+arm)*.016f,Mathf.Sin(t*9+arm)*.012f),
                            t=>(.009f+.055f*Mathf.Sin(t*Mathf.PI))*(.48f+.52f*Mathf.Pow(Mathf.Sin(t*14+arm),2)),38,1,j*.29f);
                    }
                    break;
                case 11:
                    shape.Liquid(Vector3.zero,Vector3.one*.14f,.3f,.6f);
                    for(int j=0;j<3;j++)
                    {
                        int arm=j;
                        Quaternion tilt=Quaternion.Euler(19+j*9,j*14,11-j*12);
                        shape.Ribbon(t=>tilt*Orbit(arm*2.399f+t*5.55f+.19f*Mathf.Sin(t*7+arm),.035f+Mathf.Pow(t,1.4f)*.41f,
                            (t-.44f)*.17f+Mathf.Sin(t*8+arm)*.046f),
                            t=>(.017f+.048f*Mathf.Sin(t*Mathf.PI))*(1-t*.57f),36,1,j*.21f);
                    }
                    for(int j=0;j<7;j++)shape.Rock(Orbit(j*2.399f,.27f+(j%3)*.068f,-.25f+(j%4)*.17f),
                        new Vector3(.069f+(j%3)*.021f,.084f+(j%2)*.037f,.061f+(j%3)*.014f),.17f+j*.123f,0,8,4);
                    break;
                case 12:
                    shape.Rock(Vector3.zero,new Vector3(.29f,.34f,.26f),3,0,9,4);
                    for(int j=0;j<16;j++)
                    {
                        Vector3 direction=Orbit(j*2.399f,.7f,-.9f+(j%7)*.3f).normalized;
                        shape.Shard(direction*.25f,direction,.018f+(j%3)*.005f,.18f+(j%4)*.045f,0);
                    }
                    for(int j=0;j<2;j++)
                    {
                        int line=j;
                        Quaternion field=Quaternion.Euler(18+j*22,0,j*24-12);
                        shape.Ribbon(t=>field*new Vector3(Mathf.Sin(t*Mathf.PI*2)*(.35f+line*.014f),.22f*Mathf.Sin(t*Mathf.PI*4),
                            Mathf.Cos(t*Mathf.PI*2)*.15f),t=>.018f+.025f*Mathf.Sin(t*Mathf.PI),28,1,j*.17f);
                    }
                    break;
                case 13:
                    shape.Rock(new Vector3(.025f,.02f,0),new Vector3(.24f,.23f,.22f),1,.45f,9,4);
                    for(int j=0;j<3;j++)shape.Rock(Orbit(j*2.399f,.16f,-.07f+j*.08f),new Vector3(.13f,.12f,.11f),j+3,.45f,6,3);
                    for(int j=0;j<7;j++)
                    {
                        int root=j;
                        float a=j*2.399f;
                        Vector3 start=Orbit(a,.14f,Mathf.Sin(j*1.7f)*.11f);
                        Vector3 outer=Orbit(a+.7f,.32f,Mathf.Sin(j*2.3f)*.16f);
                        Vector3 end=Orbit(a+1.65f,.13f,Mathf.Cos(j*1.9f)*.1f);
                        shape.Tube(t=>Vector3.Lerp(Vector3.Lerp(start,outer,t),Vector3.Lerp(outer,end,t),t)+
                            new Vector3(0,Mathf.Sin(t*Mathf.PI)*Mathf.Sin(root)*.045f,0),
                            t=>.013f+.009f*Mathf.Sin(t*Mathf.PI),18,4,0);
                    }
                    for(int j=0;j<23;j++)
                    {
                        Vector3 point=Orbit(j*2.399f,.15f+(j%3)*.06f,-.08f+(j%5)*.089f);
                        shape.SmallLeaf(point,Orbit(j*2.11f,.15f+(j%3)*.025f,.11f+(j%5)*.024f),.045f+(j%3)*.012f,1);
                    }
                    break;
                default: throw new ArgumentOutOfRangeException(nameof(style));
            }
            Mesh result=shape.Create(id);
            if(style==11)
            {
                float radius=0;
                foreach(Vector3 point in result.vertices)radius=Mathf.Max(radius,new Vector2(point.x,point.z).magnitude);
                Bounds bounds=result.bounds;
                bounds.center=new Vector3(0,bounds.center.y,0);
                bounds.extents=new Vector3(radius+.18f,bounds.extents.y,radius+.18f);
                result.bounds=bounds;
            }
            return result;
        }

        private static Mesh BuildFragment(string id,int style)
        {
            var shape=new Shape();
            if(style==0)shape.SmallDrop(Vector3.zero,.34f,.8f,0);
            else if(style==4||style==8)shape.Shard(Vector3.zero,new Vector3(.1f,1,.1f),.25f,.9f,style==4?0:.7f);
            else if(style==13||style==2)shape.SmallLeaf(new Vector3(0,-.35f,0),Vector3.up*.7f,.2f,style==13?1:0);
            else shape.Rock(Vector3.zero,new Vector3(.34f,.45f,.3f),2,0,6,3);
            return shape.Create(id+"-debris",3);
        }

        private static Vector3 LightningFork(float t,int branch)
        {
            float joint=t*8,step=Mathf.Min(7,Mathf.Floor(joint)),fraction=joint-step;
            Vector3 Leader(float s)=>new Vector3(-.46f+s*.92f,Mathf.Sin(s*13)*.10f,Mathf.Sin(s*9)*.12f);
            float attachment=branch/10f;
            Vector3 root=branch==0?Leader(0):LightningFork(attachment,0);
            Vector3 end=branch==0?Leader(1):root+Orbit(branch*2.399f,.29f,Mathf.Sin(branch*1.7f)*.31f);
            // 한 주 방전에 옆 가지를 연결해 중앙에서 사방으로 뻗는 구형 장식과 구분한다.
            Vector3 Point(float s)=>(branch==0?Leader(s):Vector3.Lerp(root,end,s))+
                new Vector3(Mathf.Sin(s*57+branch)*.046f,Mathf.Sin(s*39+branch*2)*.046f,Mathf.Cos(s*71+branch)*.035f)*Mathf.Sin(s*Mathf.PI);
            return Vector3.Lerp(Point(step/8),Point((step+1)/8),fraction);
        }

        private sealed partial class Shape
        {
            public void WaterMass()
            {
                Surface((u,v)=>{
                    float a=u*Mathf.PI*2,p=v*Mathf.PI,s=Mathf.Sin(p),y=Mathf.Cos(p);
                    float bulge=1+.16f*Mathf.Sin(a*3+p*2)+.085f*Mathf.Cos(a*2-p*3);
                    return new Vector3(Mathf.Cos(a)*s*.33f*bulge+y*y*.045f-y*.027f,
                        y*.32f-.06f+Mathf.Sin(a*2+p)*s*.065f,
                        Mathf.Sin(a)*s*.34f*bulge+y*.09f+Mathf.Sin(p*2+.4f)*.035f);
                },24,18,0,.2f);
            }

            public void WaterSheet(float side)
            {
                Surface((u,v)=>{
                    float across=u*2-1;
                    float curl=v*3.65f-.38f;
                    float edge=1-across*across*.38f;
                    float radius=.28f+side*.014f;
                    // 양쪽 표면 사이의 두께와 말린 마루를 실제 3D 형상으로 만든다.
                    return new Vector3(across*(.30f+.035f*Mathf.Sin(v*5+across)),
                        -.04f+Mathf.Sin(curl)*radius*1.48f*edge+v*.11f,
                        -.035f+Mathf.Cos(curl)*radius+across*.055f);
                },12,18,.68f,side>0?.24f:.58f);
            }

            public void Liquid(Vector3 center,Vector3 scale,float phase,float accent=0)
            {
                Surface((u,v)=>{
                    float a=u*Mathf.PI*2,p=v*Mathf.PI;
                    Vector3 direction=new Vector3(Mathf.Cos(a)*Mathf.Sin(p),Mathf.Cos(p),Mathf.Sin(a)*Mathf.Sin(p));
                    float fold=1+.09f*Mathf.Sin(a*3+phase+p*2)*Mathf.Sin(p)+.04f*Mathf.Sin(a*5-p*4+phase);
                    return center+Vector3.Scale(direction,scale)*fold+new Vector3(Mathf.Sin(p*2+phase)*.065f,0,Mathf.Sin(p*3)*.025f);
                },20,14,accent,phase);
            }

            public void SmallDrop(Vector3 center,float radius,float height,float accent)
            {
                Surface((u,v)=>{
                    float a=u*Mathf.PI*2,w=Mathf.Sin(v*Mathf.PI)*radius*(.6f+v*.65f);
                    return center+new Vector3(Mathf.Cos(a)*w,(.5f-v)*height,Mathf.Sin(a)*w);
                },7,6,accent,accent);
            }

            public void Ribbon(Func<float,Vector3> path,Func<float,float> width,int segments,float accent,float phase=-1)
            {
                int start=vertices.Count;
                for(int j=0;j<=segments;j++)
                {
                    float t=j/(float)segments;
                    Vector3 tangent=(path(Mathf.Min(1,t+.002f))-path(Mathf.Max(0,t-.002f))).normalized;
                    Vector3 across=Vector3.Cross(tangent,Mathf.Abs(tangent.y)<.9f?Vector3.up:Vector3.forward).normalized;
                    for(int side=0;side<=2;side++)
                    {
                        Vertex(path(t)+across*((side-1)*width(t)),new Vector2(t,side*.5f),accent,phase<0?accent:phase);
                        if(phase>=0)
                        {
                            int index=colors.Count-1;
                            Color detail=colors[index];detail.b=.5f;colors[index]=detail;
                        }
                    }
                }
                Grid(start,segments,2);
            }

            public void Rock(Vector3 center,Vector3 scale,float seed,float accent,int columns,int rows)
            {
                Vector3 Point(int x,int y)
                {
                    float angle=x*Mathf.PI*2/columns,latitude=y*Mathf.PI/rows;
                    Vector3 direction=new Vector3(Mathf.Cos(angle)*Mathf.Sin(latitude),Mathf.Cos(latitude),Mathf.Sin(angle)*Mathf.Sin(latitude));
                    float grain=.8f+.17f*Mathf.Sin(direction.x*11+seed)*Mathf.Cos(direction.y*9-seed)+.1f*Mathf.Sin(direction.z*17+seed);
                    return center+Vector3.Scale(direction,scale)*grain;
                }
                for(int y=0;y<rows;y++)for(int x=0;x<columns;x++)
                {
                    Vector3 a=Point(x,y),b=Point(x+1,y),c=Point(x,y+1),d=Point(x+1,y+1);
                    // 평평한 면의 법선은 유지하고 표면 지도 좌표만 이웃 면과 연결한다.
                    var ua=new Vector2(x/(float)columns,y/(float)rows)+new Vector2(seed*.37f,seed*.29f);
                    var ub=ua+new Vector2(1f/columns,0);
                    var uc=ua+new Vector2(0,1f/rows);
                    var ud=new Vector2(ub.x,uc.y);
                    RockFace(a,b,c,ua,ub,uc,accent,seed);
                    RockFace(b,d,c,ub,ud,uc,accent,seed);
                }
            }

            private void RockFace(Vector3 a,Vector3 b,Vector3 c,Vector2 ua,Vector2 ub,Vector2 uc,float accent,float phase)
            {
                int start=vertices.Count;
                Vertex(a,ua,accent,phase);Vertex(b,ub,accent,phase);Vertex(c,uc,accent,phase);
                Triangle(start,start+1,start+2);
            }

            public void Shard(Vector3 center,Vector3 direction,float radius,float height,float accent,float thickness=1,int sides=5)
            {
                Quaternion rotation=Quaternion.FromToRotation(Vector3.up,direction.normalized);
                Vector3 Point(Vector3 p) => center+rotation*new Vector3(p.x,p.y,p.z*thickness);
                for(int j=0;j<sides;j++)
                {
                    float angle=j*Mathf.PI*2/sides,next=(j+1)*Mathf.PI*2/sides;
                    Vector3 a=Orbit(angle,radius,-height*.33f),b=Orbit(next,radius,-height*.33f);
                    Vector3 c=Orbit(angle,radius*.82f,height*.23f),d=Orbit(next,radius*.82f,height*.23f);
                    Vector3 top=new Vector3(radius*.28f,height*.58f,-radius*.17f),bottom=new Vector3(-radius*.15f,-height*.46f,radius*.1f);
                    Face(Point(a),Point(c),Point(b),accent,j*.17f);
                    Face(Point(b),Point(c),Point(d),accent,j*.17f);
                    Face(Point(c),Point(top),Point(d),accent,j*.17f);
                    Face(Point(a),Point(b),Point(bottom),accent,j*.17f);
                }
            }

            public void SmallLeaf(Vector3 root,Vector3 direction,float width,float accent)
            {
                Vector3 axis=direction.normalized;
                Vector3 side=Vector3.Cross(axis,Mathf.Abs(axis.y)<.93f?Vector3.up:Vector3.forward).normalized;
                Vector3 normal=Vector3.Cross(side,axis);
                Surface((u,v)=>{
                    float cross=u*2-1;
                    float span=Mathf.Pow(Mathf.Max(0,Mathf.Sin(v*Mathf.PI)),.85f);
                    return root+direction*v+side*(cross*width*span)+normal*((1-Mathf.Abs(cross))*span*.025f+v*v*.037f);
                },4,5,accent,Mathf.Abs(root.x*3+root.z*5));
            }
        }
    }
}
