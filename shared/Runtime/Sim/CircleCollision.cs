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
            float radiusSquared=radius*radius;
            Vector2 offset=start-center;float b=Vector2.Dot(offset,delta),c=offset.LengthSquared()-radiusSquared;
            if(c<0||b>=0)return fraction;
            float discriminant=b*b-lengthSquared*c;if(discriminant<0)return fraction;
            float hit=(-b-(float)Math.Sqrt(discriminant))/lengthSquared;
            if (hit < 0 || hit > fraction) return fraction;
            float safe = Math.Max(0, hit - 0.0001f);
            // 월드 좌표 반올림으로 접촉점이 원 안에 들어가면 다음 틱이 내부 출발로 처리된다.
            while (safe > 0 && Vector2.DistanceSquared(start + delta * safe, center) < radiusSquared)
                safe *= 0.5f;
            return safe;
        }
    }
}
