param([Parameter(Mandatory = $true)][string]$GameRoot)
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
Add-Type -Path (Join-Path $GameRoot "BepInEx/core/Mono.Cecil.dll")
$rp=[Mono.Cecil.ReaderParameters]::new()
$rp.AssemblyResolver=[Mono.Cecil.DefaultAssemblyResolver]::new()
$rp.AssemblyResolver.AddSearchDirectory((Join-Path $GameRoot "EscapeFromTarkov_Data/Managed"))
$rp.AssemblyResolver.AddSearchDirectory((Join-Path $GameRoot "BepInEx/core"))
$types=@{}
function Index-Type($t){$types[$t.FullName]=$t;foreach($n in $t.NestedTypes){Index-Type $n}}
Get-ChildItem (Join-Path $GameRoot "EscapeFromTarkov_Data/Managed/*.dll") | ForEach-Object {
 $a=[Mono.Cecil.AssemblyDefinition]::ReadAssembly($_.FullName,$rp)
 foreach($t in $a.MainModule.Types){Index-Type $t}
}
$plugin=[Mono.Cecil.AssemblyDefinition]::ReadAssembly("$repositoryRoot/_build/JustachaeThirdPersonCamera.dll",$rp)
foreach($t in $plugin.MainModule.Types){Index-Type $t}
$checks=@(
 @('EFT.PlayerAnimator','SetAiming','AimArms','SetAimingPrefix'),
 @('PlayerBones','RotateHead','AimArms','RotateHeadPrefix'),
 @('PlayerBones','RotateHead','AimArms','RotateHeadPostfix'),
 @('FirearmsAnimator','SetFireMode','AimArms','FireModePostfix'),
 @('FirearmsAnimator','TriggerFiremodeCheck','AimArms','FireModeCheckPostfix'),
 @('EFT.Player/FirearmController','AdjustShotVectors','AimArms','AdjustShotPrefix'),
 @('PlayerPhysicalClass','Aim','AimArms','AimPrefix'),
 @('PlayerPhysicalClass','HoldBreath','AimArms','HoldBreathPostfix'),
 @('EFT.Player','VisualPass','AimArms','VisualPassPostfix'),
 @('EFT.Player','Rotate','AimArms','RotatePrefix'),
 @('EFT.Player','Look','AimArms','LookPrefix'),
 @('EFT.Player','Look','AimArms','LookPostfix'),
 @('EFT.Player','get_IsVisible','AimArms','VisiblePostfix'),
 @('CameraClass','SetFov','AimArms','SetFovPrefix'),
 @('LaserBeam','LateUpdate','AimArms','LaserLateUpdatePrefix'),
 @('EFT.PlayerBody','UpdatePlayerRenders','BodyVisibility','UpdatePlayerRendersPostfix'),
 @('EFT.PlayerBody/GClass2119','method_4','BodyVisibility','ParentEquipmentModelPostfix'),
 @('BaseSoundPlayer/PlayerBridge','get_PointOfView','FirstPersonAudio','PlayerBridgePointOfViewPostfix'),
 @('UnityEngine.Rendering.PostProcessing.PostProcessLayer','OnPreCull','ReticleAfterTaa','BeforePostProcess'),
 @('UnityEngine.Rendering.PostProcessing.TemporalAntialiasing','Render','ReticleAfterTaa','RenderPostfix'),
 @('SSAAPropagator','FillCommandBuffer','ReticleAfterTaa','UpscalePostfix'),
 @('VisorEffect','method_1','VisorFilter','OnRenderImageImplPrefix')
)
$failed=0
foreach($c in $checks){
 $target=@($types[$c[0]].Methods | Where-Object Name -eq $c[1])
 $patch=@($types["EFTNewCamera.$($c[2])"].Methods | Where-Object Name -eq $c[3])
 if($target.Count -ne 1 -or $patch.Count -ne 1){"FAIL target $($c -join ' ')";$failed++;continue}
 foreach($p in $patch[0].Parameters){
  if($p.Name -eq '__instance'){$expected=$c[0]}
  elseif($p.Name -eq '__result'){$expected=$target[0].ReturnType.FullName}
  elseif($p.Name -eq '__0'){$expected=$target[0].Parameters[0].ParameterType.FullName}
  else{$tp=@($target[0].Parameters | Where-Object Name -eq $p.Name);if($tp.Count -ne 1){"FAIL missing parameter $($c[3]).$($p.Name)";$failed++;continue};$expected=$tp[0].ParameterType.FullName.TrimEnd('&')}
  $actual=$p.ParameterType.FullName.TrimEnd('&')
  $ok=$actual -eq $expected
  if(!$ok -and $p.Name -eq '__instance'){
   $base=$types[$expected].BaseType
   while($null -ne $base -and !$ok){if($base.FullName -eq $actual){$ok=$true;break};if(!$types.ContainsKey($base.FullName)){break};$base=$types[$base.FullName].BaseType}
  }
  if(!$ok){"FAIL parameter $($c[3]).$($p.Name) $actual expected $expected";$failed++}
 }
 "CHECK $($c[0]).$($c[1]) -> $($c[3])"
}
foreach($f in @(@('EFT.Player/FirearmController','WeaponLn'),@('EFT.Animations.ProceduralWeaponAnimation','_currentLineOfSightDeltaAngle'),@('CameraClass','cameraClass'),@('VisorEffect','gameObject_0'),@('UnityEngine.Rendering.PostProcessing.PostProcessLayer','_isTAAResolveDisabled'))){
 if(@($types[$f[0]].Fields | Where-Object Name -eq $f[1]).Count -ne 1){"FAIL field $f";$failed++}else{"CHECK field $($f -join '.')"}
}
$audio=@('method_31','PlaySpeechFromTime','UpdateMuffledState','method_34','method_40','CreateNestedSource','CreateSpeechSource','CreateGearSource','method_43','method_44','method_46','PlayInteractionSound','StopInteractionSound','method_53','DefaultPlay','PlayStepSound','method_59','PlaySoundBank','method_78','SpawnInHands')
foreach($n in $audio){
 $m=@($types['EFT.Player'].Methods | Where-Object Name -eq $n)
 $reads=@($m[0].Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.DeclaringType.FullName -eq 'EFT.Player' -and $_.Operand.Name -in @('get_PointOfView','get_FirstPersonPointOfView') }).Count
 if($m.Count -ne 1 -or $reads -eq 0){"FAIL audio $n reads=$reads";$failed++}else{"CHECK audio $n reads=$reads"}
}
foreach($n in @('method_69','method_71')){
 $m=$types['EFT.Player'].Methods | Where-Object Name -eq $n
 $ctor=$m.Body.Instructions | Where-Object { $_.OpCode.Name -eq 'newobj' -and $_.Operand.DeclaringType.DeclaringType.FullName -eq 'EFT.Player' } | Select-Object -First 1
 $sm=$types[$ctor.Operand.DeclaringType.FullName]
 $move=$sm.Methods | Where-Object Name -eq 'MoveNext'
 if(!$move){"FAIL iterator $n";$failed++}else{"CHECK iterator $n -> $($sm.FullName).MoveNext"}
}
$shot=$types['EFT.Player'].Methods | Where-Object Name -eq 'OnMakingShot'
$reads=@($shot.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'get_FirstPersonPointOfView' }).Count
if($reads -ne 2){"FAIL mounted recoil getter count=$reads";$failed++}else{"CHECK mounted recoil getter count=2"}
"Failures: $failed"
if($failed){exit 1}
