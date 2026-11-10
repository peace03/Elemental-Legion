# 시설 모델을 반영한 목표 시안

2026-11-10. 기존 `01-aaa-ingame-v1.png`를 편집 대상으로 삼고, 사용자가 제공한 시설 모델 5종을 별도 Blender 백그라운드 장면에서 렌더해 imagegen의 참고 이미지로 사용했다. 게임 코드·Unity 자산이나 원본 모델은 변경하지 않는다.

| 시설 | 확인한 소스 |
| --- | --- |
| 원시핵 | `C:/Project/3D_Model/Elemental-Legion/Primitive nucleus/Primitive_Nucleus.blend` |
| 점화로 | `C:/Project/3D_Model/Elemental-Legion/Ignition Hearth/Ignition_Hearth.fbx` |
| 수호 위성 | `C:/Project/3D_Model/Elemental-Legion/Guarding satellite/Guarding_Satellite.fbx` |
| 포집기 | `C:/Project/3D_Model/Elemental-Legion/Collecting_Apparatus/Collecting_Apparatus.fbx` |
| 응집로 | `C:/Project/3D_Model/Elemental-Legion/Cohesion Hearth/Cohesion Hearth.fbx` |

원시핵의 지정된 FBX는 가져오기 결과 메시가 0개였다. 같은 폴더의 동명 Blender 원본에는 표시 가능한 메시 2개와 재질 2개가 있어, 이 완성 조합으로 참고 이미지를 만들었다. 초기 GLB 임시 조합 렌더는 최종 참고에서 제외했다. 다른 4종은 지정된 FBX를 직접 가져왔다. 내장된 이미지가 있으면 외부 파일 경로가 없어도 내장 데이터를 사용했다.

참고 렌더와 가져오기 근거는 `.artifacts/ingame-target-20261110/model-references/`의 PNG, `report.json`, `primitive_blend_report.json`, `audit_blend.log`에 있다. 원본 모델과 현재 열려 있는 Blender·Unity 장면은 저장하거나 수정하지 않았다. 생성 도구는 내장 imagegen이며, 최종 프롬프트는 `prompt-user-models-v2.txt`에 보존한다.

결과는 목표 외형을 표현한 생성 이미지다. 실제 Unity 모델 설치, 실시간 조명·VFX 구현, Play와 성능 측정은 이 작업의 범위에 포함하지 않는다.
