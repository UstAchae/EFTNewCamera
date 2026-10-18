param()
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$source = [IO.File]::ReadAllText((Join-Path $root 'AimArms.cs'))
$match = [regex]::Match($source, '(?s)    private static void SetStrategyPrefix\(.*?(?=    private static void VisiblePostfix)')
if (!$match.Success) { throw 'Production strategy prefix not found.' }
# Test the production prefix against the stale Init POV scenario with minimal
# managed substitutes. This does not load Unity or execute Harmony patches.
$fixture = @'
#nullable enable
using System;
using EFT;
using EFT.Animations;
using Comfort.Common;
namespace EFT {
 public enum EPointOfView { FirstPerson, ThirdPerson, FreeCamera }
 public class GameWorld { public Player? MainPlayer; }
 public class Player {
  public bool IsYourPlayer = true;
  public EPointOfView PointOfView;
  public ProceduralWeaponAnimation ProceduralWeaponAnimation = new();
 }
}
namespace EFT.Animations { public class ProceduralWeaponAnimation {} }
namespace Comfort.Common { public static class Singleton<T> { public static T? Instance; } }
namespace EFTNewCamera {
 internal static class NewCameraSwitch { public static bool Enabled; }
 public static class StationaryStrategyRegression {
__PRODUCTION_PREFIX__
  private static void Check(bool condition, string message) {
   if (!condition) throw new Exception(message);
  }
  public static string Run() {
   var player = new Player { PointOfView = EPointOfView.ThirdPerson };
   Singleton<GameWorld>.Instance = new GameWorld { MainPlayer = player };
   NewCameraSwitch.Enabled = true;
   // Native stationary exit uses the POV captured before the mod was enabled.
   var requested = EPointOfView.FirstPerson;
   SetStrategyPrefix(player.ProceduralWeaponAnimation, ref requested);
   Check(requested == EPointOfView.ThirdPerson, "Stationary exit restored the stale first-person strategy");
   requested = EPointOfView.ThirdPerson;
   SetStrategyPrefix(player.ProceduralWeaponAnimation, ref requested);
   Check(requested == EPointOfView.ThirdPerson, "Normal third-person strategy changed");
   requested = EPointOfView.FirstPerson;
   SetStrategyPrefix(new ProceduralWeaponAnimation(), ref requested);
   Check(requested == EPointOfView.FirstPerson, "Another animation instance was modified");
   NewCameraSwitch.Enabled = false;
   SetStrategyPrefix(player.ProceduralWeaponAnimation, ref requested);
   Check(requested == EPointOfView.FirstPerson, "Disabled mod still overrode strategy");
   NewCameraSwitch.Enabled = true;
   player.PointOfView = EPointOfView.FreeCamera;
   SetStrategyPrefix(player.ProceduralWeaponAnimation, ref requested);
   Check(requested == EPointOfView.FirstPerson, "Free camera strategy was modified");
   player.PointOfView = EPointOfView.ThirdPerson;
   player.IsYourPlayer = false;
   SetStrategyPrefix(player.ProceduralWeaponAnimation, ref requested);
   Check(requested == EPointOfView.FirstPerson, "Non-local player strategy was modified");
   return "PASS: 6 managed strategy scenarios (production prefix; Unity/Harmony not executed).";
  }
 }
}
'@
Add-Type -TypeDefinition ($fixture.Replace('__PRODUCTION_PREFIX__', $match.Value))
[EFTNewCamera.StationaryStrategyRegression]::Run()
