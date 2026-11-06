using System.Runtime.CompilerServices;

// Editor 검증에서 실제 화면 생성 코드를 재사용한다. 런타임 공개 API를 늘리지 않는다.
[assembly: InternalsVisibleTo("ElementalLegion.Client.Editor")]
[assembly: InternalsVisibleTo("ElementalLegion.Client.EditModeTests")]
