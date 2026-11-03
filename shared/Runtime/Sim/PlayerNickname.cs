namespace ElementalLegion.Shared.Sim
{
    /// <summary>공개 닉네임에 사용할 수 있는 문자와 길이를 검사한다.</summary>
    public static class PlayerNickname
    {
        public const string Default = "플레이어";
        public const int MaxLength = 16;

        public static bool IsValid(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > MaxLength || value[0] == ' ' || value[value.Length - 1] == ' ')
                return false;
            bool hasLetterOrDigit = false;
            foreach (char character in value)
            {
                // 한글 채움 문자는 글자로 분류되지만 화면에 이름이 보이지 않을 수 있다.
                if (character == '\u115f' || character == '\u1160' || character == '\u3164' || character == '\uffa0') return false;
                if (char.IsLetterOrDigit(character)) hasLetterOrDigit = true;
                else if (character != ' ' && character != '-' && character != '_') return false;
            }
            return hasLetterOrDigit;
        }
    }
}
