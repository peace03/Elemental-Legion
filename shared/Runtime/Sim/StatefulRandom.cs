// .NET Foundation and Contributors의 MIT 코드에서 시드 호환 난수 알고리즘을 가져왔다.
// 원본은 dotnet/runtime v10.0.0 Random.CompatImpl.cs이며 고지는 StatefulRandom.LICENSE.txt에 있다.
using System;
using Newtonsoft.Json;

namespace ElementalLegion.Shared.Sim
{
    /// <summary>System.Random의 시드 기반 수열과 호환되며 승계에 필요한 내부 상태를 복사한다.</summary>
    internal sealed class StatefulRandom : Random
    {
        private readonly int[] seeds = new int[56];
        private int next, nextPartner = 21;

        internal StatefulRandom(int seed)
        {
            int subtraction = seed == int.MinValue ? int.MaxValue : Math.Abs(seed);
            int previous = 161803398 - subtraction;
            seeds[55] = previous;
            int current = 1;
            for (int index = 1; index < 55; index++)
            {
                int slot = 21 * index % 55;
                seeds[slot] = current;
                current = previous - current;
                if (current < 0) current += int.MaxValue;
                previous = seeds[slot];
            }
            for (int pass = 0; pass < 4; pass++)
                for (int index = 1; index < 56; index++)
                {
                    seeds[index] -= seeds[1 + (index + 30) % 55];
                    if (seeds[index] < 0) seeds[index] += int.MaxValue;
                }
        }

        internal StatefulRandomState CaptureState() => new StatefulRandomState
        {
            Seeds = (int[])seeds.Clone(), Next = next, NextPartner = nextPartner
        };

        internal void RestoreState(StatefulRandomState state)
        {
            WorldCheckpoint.Check(state != null && state.Seeds != null && state.Seeds.Length == seeds.Length &&
                state.Seeds[0] == 0 && state.Next >= 0 && state.Next < 56 && state.NextPartner > 0 && state.NextPartner < 56 &&
                (state.NextPartner - state.Next + 55) % 55 == 21, "난수 상태");
            foreach (int value in state.Seeds) WorldCheckpoint.Check(value >= 0 && value < int.MaxValue, "난수 배열");
            Array.Copy(state.Seeds, seeds, seeds.Length);
            next = state.Next;
            nextPartner = state.NextPartner;
        }

        public override int Next() => NextSample();

        public override int Next(int maxValue)
        {
            if (maxValue < 0) throw new ArgumentOutOfRangeException(nameof(maxValue));
            return (int)(Sample() * maxValue);
        }

        public override int Next(int minValue, int maxValue)
        {
            if (minValue > maxValue) throw new ArgumentOutOfRangeException(nameof(minValue));
            long range = (long)maxValue - minValue;
            return range <= int.MaxValue ? (int)(Sample() * range) + minValue : (int)((long)(LargeSample() * range) + minValue);
        }

        public override double NextDouble() => Sample();
        protected override double Sample() => NextSample() * (1.0 / int.MaxValue);

        public override void NextBytes(byte[] buffer)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            NextBytes(buffer.AsSpan());
        }

        public override void NextBytes(Span<byte> buffer)
        {
            for (int index = 0; index < buffer.Length; index++) buffer[index] = (byte)NextSample();
        }

        private int NextSample()
        {
            if (++next >= 56) next = 1;
            if (++nextPartner >= 56) nextPartner = 1;
            int value = seeds[next] - seeds[nextPartner];
            if (value == int.MaxValue) value--;
            if (value < 0) value += int.MaxValue;
            seeds[next] = value;
            return value;
        }

        private double LargeSample()
        {
            int value = NextSample();
            if (NextSample() % 2 == 0) value = -value;
            double result = value;
            result += int.MaxValue - 1;
            return result / (2u * int.MaxValue - 1);
        }
    }

    /// <summary>월드가 소유한 난수 수열의 유한한 복원 상태다.</summary>
    [JsonObject(ItemRequired = Required.Always)]
    internal sealed class StatefulRandomState
    {
        public int[] Seeds;
        public int Next, NextPartner;
    }
}
