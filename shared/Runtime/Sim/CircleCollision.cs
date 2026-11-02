using System;
using System.Numerics;

namespace ElementalLegion.Shared.Sim
{
    /// <summary>서버 이동과 관측 건물의 클라이언트 예측이 공유하는 선분–원 접촉 계산이다.</summary>
    public static class CircleCollision
    {
        public static float Limit(Vector2 start,Vector2 delta,Vector2 center,float radius,float fraction=1)
        {
            float lengthSquared=delta.LengthSquared();if(lengthSquared==0)return fraction;
            Vector2 offset=start-center;float b=Vector2.Dot(offset,delta),c=offset.LengthSquared()-radius*radius;
            if(c<0||b>=0)return fraction;
            float discriminant=b*b-lengthSquared*c;if(discriminant<0)return fraction;
            float hit=(-b-(float)Math.Sqrt(discriminant))/lengthSquared;
            // 경계 오차 때문에 다음 이동이 건물을 관통하지 않도록 접촉 직전에 멈춘다.
            return hit>=0&&hit<=fraction?Math.Max(0,hit-0.0001f):fraction;
        }
    }
}
