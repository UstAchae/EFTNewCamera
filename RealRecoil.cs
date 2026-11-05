using System;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using UnityEngine;

namespace EFTNewCamera;

// Recoil from the physics of a standing shooter instead of the game's random kick.
//
// The shot's momentum (bullet plus powder gas) goes into a body of springs and dampers: the waist (pitch, yaw) under
// the upper body, the shoulder pocket (the gun sliding back, pitching and yawing against the upper body) and the neck
// (pitch, yaw), with the cheek on the stock tying the head to the gun. Each shot pushes three times: the gas, the bolt
// carrier hitting the rear, and the carrier slamming home (a push forward). A delayed pull back to the aim stands for
// the shooter's own correction, which grows over the first shots of a burst.
//
// Model and numbers: .codex-work/notes/recoil-research.md and recoil-design.md; the same model runs in
// .codex-work/recoil-sim/index.html. The model is linear about the rest pose: the angles stay within a few degrees.
internal static class RealRecoil
{
    internal static bool Enabled = true;

    // Coordinates: 0 waist pitch (up +), 1 waist yaw (left +), 2 gun slide back (m), 3 gun pitch to the upper body (up +),
    // 4 gun yaw to the upper body (left +), 5 head pitch to the upper body (up +), 6 head yaw to the upper body (left +).
    private const int N = 7;
    private const double Step = 1.0 / 4000.0;
    private const double MaxDelay = 0.4;
    private const int HistoryLength = (int)(MaxDelay / Step) + 2;

    // Shooter (研究 §9 阎舜 2016 table 1 mid values, scaled 2.5x with the waist pitch damping halved to return a
    // single shot in about 250 ms: the simulator's first calibration).
    private const double BodyScale = 2.5;
    // The waist's damping is set so a shot's climb comes back without bouncing past the aim: at half the mid value it
    // returned fast but swung below and back (the "bounce" seen in game); at twice it settles in about half a second,
    // near the ~0.8 s restoring stage measured after a burst (研究 §13).
    private const double WaistPitchDamping = 2.0;
    private const double NerveDelay = 0.2;
    private const double WaistYawStiffness = 4.0;   // N·m/deg, low end of Yan Shun's range
    private const double WaistYawDamping = 1.0;     // N·m·s/deg, below mid: sideways stays the weaker hold, without bouncing
    private const double YawControl = 0.5;          // the pull back sideways, against the pull back up and down
    private const double ControlP = 700.0, ControlD = 60.0;
    private const int AdaptShots = 5;
    private const double Stamina = 0.8;
    private const double BurstGap = 0.8;
    // Powder gas leaves at about 1.75x the bullet (SAAMI, high-power rifles). With the powder taken from the muzzle
    // energy below, this puts 5.56 at 5.6 N·s, inside the 5.5–6.1 N·s measured on a ballistic pendulum (研究 §12.2).
    private const double GasVelocityFactor = 1.75;

    // Geometry of the rest pose, in the upper body's frame from the waist: x forward, y up, z to the right.
    private static readonly double[] Shoulder = { 0.06, 0.42, 0.17 };
    private static readonly double[] Neck = { 0.02, 0.50, 0.09 };
    private static readonly double[] Cheek = { 0.05, -0.07, 0.07 };   // on the head, from the neck

    private static readonly double[] _q = new double[N], _qd = new double[N], _force = new double[N], _qdd = new double[N];
    private static readonly double[,] _minv = new double[N, N], _k = new double[N, N], _d = new double[N, N];
    private static readonly double[] _bBore = new double[N], _bCarrier = new double[N], _bMuzzleDown = new double[N];
    // How hard a push down at the muzzle has to be, per unit of the shot's push back, to take back all its climb.
    private static double _climbPerDown;
    private static readonly double[] _errPitch = new double[HistoryLength], _errYaw = new double[HistoryLength];
    private static readonly double[] _errPitchRate = new double[HistoryLength], _errYawRate = new double[HistoryLength];
    private static int _historyHead;
    private static long _stepCount;

    private struct Shot
    {
        internal double Time, Impulse, Beta, Cycle;
        // The shooter's hold, never the same twice: a random push about the waist, up-down and sideways (N·m·s).
        internal double HoldPitch, HoldYaw;
        // A compensator's push down at the muzzle (N·s), from the gas it throws up.
        internal double MuzzleDown;
    }

    // The muzzle devices, by the game's own muzzle type and its recoil figure. In life a muzzle brake does far more than
    // the game's few percent (研究 §14: 30–45 % of the impulse measured), so: a brake takes 8x its figure off the
    // impulse, a silencer 5x; a compensator 8x its figure off the climb instead, pushing the muzzle down; anything else
    // its own figure. The impulse taken never passes the share the powder gas carries.
    private const double BrakeFactor = 8.0, SilencerFactor = 5.0, CompensatorFactor = 8.0;
    private const double MaxClimbTaken = 0.9;
    private static double _impulseTaken, _climbTaken;
    // A brake can take more than the gas's share: ports angled back throw the gas rearwards. Measured, the best take about
    // 1.2x the share worked out here (研究 §14); in theory at most 2x.
    private const double MaxGasShareTaken = 1.5;

    // The stock and grips, by the game's own mod classes. None of these change the impulse in life; they change how the
    // body takes it. Game estimates, not measured:
    // - a stock's recoil figure softens the shoulder (more damping in the pocket and on the gun's pitch) and the jolt to
    //   the cheek (研究 §3: the buffered stock gave the least head motion);
    // - no part touching the shoulder (no stock, or folded): the hands and wrists take the gun, not the pocket;
    // - a foregrip helps hold the gun, sideways more (the weaker way, 研究 §13);
    // - the pistol grip, by its ergonomics alone (every one in the game has a recoil figure of 0): the right hand presses
    //   the muzzle down (the grip takes most of the vertical load, 研究 §12.1) and pulls the stock into the pocket.
    private static double _stockSoft;
    private static bool _shouldered = true;
    private static double _pitchHold = 1.0, _yawHold = 1.0;
    private static double _pocketFirm = 1.0;
    private const double GripPressPerErgonomics = 0.03, GripPullPerErgonomics = 0.02;
    private const double HandsBoreHeight = 0.08;   // bore over the gripping hand, without the shoulder

    // Shot-to-shot scatter from the hold (grip, cheek, stance shifting a little each shot). No paper gives its size:
    // set so a shot's random turn is about a third of the steady drift sideways and a fifth up and down (sideways
    // control is the weaker, 研究 §13), and it grows over a long burst as the hold loosens. A game estimate.
    private const double HoldScatterYaw = 0.35 * 0.17;    // fraction of the impulse, times the shoulder's sideways lever (m)
    private const double HoldScatterPitch = 0.20 * 0.42;  // times the shoulder's height over the waist (m)
    private const double HoldLooseningPerShot = 0.05;     // grows 5 % a shot of the burst...
    private const double HoldLooseningMax = 2.0;          // ...up to double
    private static readonly System.Random _random = new();

    private static double Gaussian()
    {
        double u1 = 1.0 - _random.NextDouble(), u2 = _random.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    private static readonly Shot[] _shots = new Shot[8];
    private static int _shotCount;
    private static double _time = -1.0;
    private static double _lastShot = -10.0;
    private static int _burst;
    private static bool _built;

    // How stiff the shoulder pocket holds the gun, back and in pitch, against the body around it (1 the body's own
    // stiffness, 2.5x the measured). Its damping goes with the square root, so it settles the same way, without a
    // bounce, only further: a 5.56 shot slides the gun back 4.7 mm at 1, 8.1 mm at 0.2.
    private static double _pocketStiffness = 0.2;
    internal static float PocketStiffness
    {
        get => (float)_pocketStiffness;
        set
        {
            double v = Math.Max(0.05, Math.Min(1.0, value));
            if (Math.Abs(v - _pocketStiffness) < 1e-6)
                return;
            _pocketStiffness = v;
            _built = false;
        }
    }
    private static bool _resting = true;

    // The weapon the model was built for, and what it took from it.
    private static Weapon? _weapon;
    private static bool _weaponDirty = true;
    private static double _weaponMass = 3.5, _boreHeight = 0.03, _length = 0.85;
    private static double _crouch = 1.0;

    // Set by the shot the model took; the game's own recoil for that shot is skipped.
    internal static bool TookShot;

    internal static bool Active => Enabled && NewCameraSwitch.Enabled;

    // Upper body about the waist, gun against the upper body, head against the upper body (radians, metres).
    internal static float WaistPitch => _pistol ? 0f : (float)_q[0];
    internal static float WaistYawLeft => _pistol ? 0f : (float)_q[1];
    internal static float GunBack => _pistol ? (float)_pq[2] : (float)_q[2];
    internal static float GunPitch => _pistol ? (float)_pq[0] : (float)_q[3];
    internal static float GunYawLeft => _pistol ? (float)_pq[1] : (float)_q[4];
    internal static float HeadPitch => _pistol ? 0f : (float)_q[5];
    internal static float HeadYawLeft => _pistol ? 0f : (float)_q[6];

    // A pistol held in the hands, with nothing on the shoulder: a model of its own. The body hardly takes part; the gun
    // turns about the wrists, the bore some centimetres over them, and the arms give back along their length. Its
    // coordinates: 0 pitch about the wrists (up +, rad), 1 yaw (left +, rad), 2 back along the arms (m). Set so a 9 mm
    // shot (about 3 N·s) snaps up some 9 degrees (two-handed shooting of a service pistol is measured at 5 to 10),
    // highest at about 45 ms, and comes back, under 2 degrees at 150 ms. The wrists are the soft joint (3.8 Hz, a little
    // under critical damping: the gun flips up freely, dipping a few hundredths of a degree past the aim at most; at
    // 2 Hz and 0.8 it rose over 80 ms and looked acted). The arms are held firm, locked out: they give only about 1 cm,
    // over some 20 ms. Neither the climb nor the slide back is drawn larger (RecoilRiseScale, RecoilTravelScale): this
    // is already the pistol as it moves.
    internal static bool Pistol => _pistol;
    private static bool _pistol;
    private static readonly double[] _pq = new double[3], _pqd = new double[3];
    private const double PistolBoreOverWrist = 0.07;   // m
    private const double PistolInertia = 0.022;         // kg·m², gun and hands about the wrists
    private const double PistolPitchHz = 3.8, PistolPitchDamping = 0.85;
    private const double PistolYawHz = 5.0, PistolYawDamping = 0.9;
    private const double PistolArmMass = 2.0;           // kg, gun and forearms along the arms
    private const double PistolBackHz = 8.0, PistolBackDamping = 1.0;
    // The slide's run back and home, whatever the game's fire rate (a pistol's is set low, which spread the slide's two
    // knocks 60 and 170 ms after the shot: two phantom shots).
    private const double PistolCycle = 0.04;
    // A rifle's carrier likewise runs back and home within this, even at a low fire rate (a semi-automatic's).
    private const double MaxCarrierCycle = 0.08;
    // The hold's scatter, as torque impulse per unit of the shot's: a fraction of the climb's lever.
    private const double PistolScatterPitch = 0.15 * PistolBoreOverWrist, PistolScatterYaw = 0.12 * PistolBoreOverWrist;
    internal static bool Resting => _resting;

    internal static void WeaponChanged()
    {
        _weaponDirty = true;
    }

    // FirearmController.Shot: the bullet has just left along the gun as it is now. The push starts here.
    internal static void ShotPostfix(Player.FirearmController __instance, Weapon weapon, Ammo ammo)
    {
        if (!Active)
            return;
        Player? player = Singleton<GameWorld>.Instance?.MainPlayer;
        if (player == null || !ReferenceEquals(player.HandsController, __instance) || weapon == null || ammo == null)
            return;
        if (Planted(player))
            return;

        if (!ReferenceEquals(weapon, _weapon) || _weaponDirty)
            Measure(player, weapon);
        double crouch = player.MovementContext != null && player.MovementContext.PoseLevel < 0.6f ? 1.6 : 1.0;
        if (!_built || crouch != _crouch)
        {
            _crouch = crouch;
            Build();
        }

        double now = Time.time;
        if (_time < 0.0)
            _time = now;
        if (now > _time)
            Advance(now);

        // Momentum: bullet (all pellets) plus powder gas. The game has no powder charge, so it comes from the muzzle
        // energy: about 1.15 MJ of muzzle energy per kilogram of powder across rifle and pistol rounds (研究 §2).
        double bullet = Math.Max(0.1, ammo.BulletMassGram) / 1000.0 * Math.Max(1, ammo.ProjectileCount);
        double speed = Math.Max(50.0, ammo.InitialSpeed * weapon.SpeedFactor);
        double powder = 0.5 * bullet * speed * speed / 1.15e6 * (1.0 + ammo.ammoRec / 100.0);
        double gasMomentum = powder * GasVelocityFactor * speed;
        double impulse = bullet * speed + gasMomentum;
        double taken = Math.Max(-0.5, Math.Min(_impulseTaken, MaxGasShareTaken * gasMomentum / impulse));
        impulse *= 1.0 - taken;
        double climbTaken = Math.Max(0.0, Math.Min(MaxClimbTaken, _climbTaken));

        double cycle = _pistol ? PistolCycle : Math.Min(MaxCarrierCycle, 60.0 / Math.Max(300, weapon.FireRate));
        // Two shots inside one frame (a fast gun at a low frame rate) are a cycle apart, not on top of each other. A
        // shot is never put behind the model's clock: the game can fire on a clock that lags the frame's (a fixed
        // step), and a push that started in the past would lose the gas and keep only the carrier's forward slam.
        double at = Math.Max(Math.Max(now, _time), _lastShot + cycle * 0.9);
        if (at - _lastShot > BurstGap)
            _burst = 0;
        _burst++;
        _lastShot = at;
        // A bolt action or pump has no carrier running in the gun; everything else does (研究 §12).
        double beta = weapon.BoltAction ? 0.0 : 0.35;
        double loose = Math.Min(HoldLooseningMax, 1.0 + HoldLooseningPerShot * (_burst - 1)) * (1.8 - Stamina);
        _shots[_shotCount % _shots.Length] = _pistol
            ? new Shot
            {
                Time = at, Impulse = impulse, Beta = beta, Cycle = cycle,
                HoldPitch = Gaussian() * PistolScatterPitch * impulse * loose,
                HoldYaw = Gaussian() * PistolScatterYaw * impulse * loose,
                MuzzleDown = climbTaken * impulse
            }
            : new Shot
            {
                Time = at, Impulse = impulse, Beta = beta, Cycle = cycle,
                HoldPitch = Gaussian() * HoldScatterPitch * impulse * loose,
                HoldYaw = Gaussian() * HoldScatterYaw * impulse * loose,
                MuzzleDown = climbTaken * _climbPerDown * impulse
            };
        _shotCount++;
        _resting = false;
        TookShot = true;
    }

    // ShotEffector.Process: the game's own kick. Skipped for a shot the model took.
    internal static bool ProcessPrefix(ShotEffector __instance)
    {
        if (!TookShot)
            return true;
        Player? player = Singleton<GameWorld>.Instance?.MainPlayer;
        if (player == null || !ReferenceEquals(__instance._firearmController, player.HandsController))
            return true;
        TookShot = false;
        return false;
    }

    // Steps the model up to now. Once a frame, before the body is posed.
    internal static void Update()
    {
        if (!Active || _resting)
            return;
        double now = Time.time;
        if (_time < 0.0)
            _time = now;
        if (now > _time)
            Advance(now);
        if (now - _lastShot > 1.5)
            Rest();
    }

    internal static void Rest()
    {
        Array.Clear(_q, 0, N);
        Array.Clear(_qd, 0, N);
        Array.Clear(_pq, 0, 3);
        Array.Clear(_pqd, 0, 3);
        Array.Clear(_errPitch, 0, HistoryLength);
        Array.Clear(_errYaw, 0, HistoryLength);
        Array.Clear(_errPitchRate, 0, HistoryLength);
        Array.Clear(_errYawRate, 0, HistoryLength);
        _shotCount = 0;
        _burst = 0;
        _resting = true;
        _time = -1.0;
    }

    private static bool Planted(Player player)
    {
        MovementContext? movement = player.MovementContext;
        return movement == null || movement.IsInMountedState || movement.IsStationaryWeaponInHands || player.IsInPronePose;
    }

    // What the model needs from the gun: its mass with everything on it. The bore's height over the shoulder pocket
    // and the length stay at a typical rifle's for now: read off the game's recoil pivot they came out anywhere from
    // -20 to +100 mm for the same gun, so that pivot is not the stock.
    private static void Measure(Player player, Weapon weapon)
    {
        _weapon = weapon;
        _weaponDirty = false;
        double mass = 0.0;
        foreach (Item item in weapon.GetAllVisibleItems())
            mass += item.Weight;
        _weaponMass = Math.Max(1.0, Math.Min(15.0, mass));
        _impulseTaken = 0.0;
        double stockSoft = 0.0, foregrip = 0.0, pistolGrip = 0.0;
        bool shouldered = false, folded = weapon.Folded;
        foreach (Item item in weapon.GetAllVisibleItems())
        {
            if (item is not Mod part)
                continue;
            bool stock = part is Stock;
            if (folded && stock)
                continue;
            if (part.Template is ModTemplate template && template.HasShoulderContact)
                shouldered = true;
            double recoilCut = -part.Recoil / 100.0, ergonomics = part.Ergonomics / 100.0;
            if (stock)
                stockSoft += recoilCut;
            else if (part is Foregrip)
                foregrip += 5.0 * recoilCut + ergonomics;
            else if (part is PistolGrip)
                pistolGrip += part.Ergonomics;
        }
        // A gun with nowhere to put a stock has it built in (a bullpup such as the AUG, MDR, RFB or ASh-12, or a fixed
        // stock), and rests on the shoulder, except a pistol. A stock slot left empty (a bare buffer tube) does not.
        bool stockSlot = false;
        foreach (Slot slot in weapon.AllSlots)
        {
            if (slot.ID != null && slot.ID.StartsWith("mod_stock", StringComparison.Ordinal))
            {
                stockSlot = true;
                break;
            }
        }
        if (!stockSlot && weapon.WeapClass != "pistol")
            shouldered = true;
        _stockSoft = Math.Max(0.0, Math.Min(0.4, stockSoft));
        _shouldered = shouldered;
        bool pistol = weapon.WeapClass == "pistol" && !shouldered;
        if (pistol != _pistol)
            Rest();
        _pistol = pistol;
        NewCameraPlugin.Log?.LogInfo($"Physical recoil: {weapon.ShortName?.Localized() ?? weapon.TemplateId.ToString()} ({weapon.WeapClass}), {(pistol ? "pistol in the hands" : shouldered ? "shouldered" : "held without the shoulder")}, {_weaponMass:F2} kg");
        foregrip = Math.Max(0.0, Math.Min(0.5, foregrip));
        _yawHold = 1.0 + 2.0 * foregrip;
        _pitchHold = (1.0 + foregrip) * Math.Max(0.5, 1.0 + GripPressPerErgonomics * pistolGrip);
        _pocketFirm = Math.Max(0.5, 1.0 + GripPullPerErgonomics * pistolGrip);
        _climbTaken = 0.0;
        foreach (Item item in weapon.GetAllVisibleItems())
        {
            if (item is not Mod mod || item.Template is not IMuzzleComponentTemplate muzzle)
                continue;
            double cut = -mod.Recoil / 100.0;
            switch (muzzle.MuzzleModType)
            {
                case EMuzzleType.brake:
                    _impulseTaken += BrakeFactor * cut;
                    break;
                case EMuzzleType.conpensator:
                    _climbTaken += CompensatorFactor * cut;
                    break;
                case EMuzzleType.silencer:
                case EMuzzleType.pms:
                    _impulseTaken += SilencerFactor * cut;
                    break;
                default:
                    _impulseTaken += cut;
                    break;
            }
        }
        _boreHeight = 0.03;
        _length = 0.85;
        _built = false;
    }

    // Mass matrix, stiffness and damping, and how a push along the bore turns into the coordinates, all about the rest
    // pose. Numeric Jacobians of the same kinematics as the simulator.
    private static void Build()
    {
        double s = BodyScale, deg = 180.0 / Math.PI;
        // The waist holds a turn sideways less well than a pitch (研究 §13, Zhang/Wang/Xu, 30th Int. Symp. on Ballistics:
        // the shooter's control of the rifle sideways is clearly weaker than fore-aft and vertical): its yaw spring and
        // damper are at the low end of Yan Shun's identified range (4 N·m/deg, 0.5 N·m·s/deg), the pitch at mid range.
        double[] k = { 12 * deg * _crouch * s, WaistYawStiffness * deg * _crouch * s, 2000 * s, 1.75 * deg * s, 12 * deg * s, 40 * s, 40 * s };
        double[] d = { 1.75 * deg * _crouch * s * WaistPitchDamping, WaistYawDamping * deg * _crouch * s, 300 * s, 1.75 * deg * s, 3 * deg * s, 1.5 * s, 1.5 * s };
        double cheekK = 6000 * s, cheekD = 60 * s;
        // The stock's softening, and the hands taking the gun when nothing rests on the shoulder.
        // The pistol grip pulls the stock into the pocket: a firmer spring there.
        k[2] *= _pocketFirm;
        double pocket = _pocketStiffness, pocketDamping = Math.Sqrt(_pocketStiffness);
        k[2] *= pocket;
        d[2] *= pocketDamping;
        k[3] *= pocket;
        d[3] *= pocketDamping;
        double soft = _stockSoft;
        d[2] *= 1.0 + 2.0 * soft;
        d[3] *= 1.0 + soft;
        double cheek = Math.Max(0.3, 1.0 - 1.5 * soft);
        if (!_shouldered)
        {
            k[2] *= 0.15;
            d[2] *= 0.3;
            k[3] *= 0.3;
            d[3] *= 0.5;
            cheek = 0.2;
        }
        cheekK *= cheek;
        cheekD *= cheek;

        // Held by the hands alone, the gun turns about the grip, well under the bore, not about the pocket.
        double rb = _shouldered ? _boreHeight : HandsBoreHeight, len = _length;
        // Elements: body (0 upper body, 1 gun with the arms, 2 head), mass, centre in the body frame, principal inertia.
        (int body, double m, double[] p, double[] i)[] elements =
        {
            (0, 38.0, new[] { 0.02, 0.30, 0.0 }, new[] { 1.2, 0.97, 1.24 }),
            (2, 5.0, new[] { 0.03, 0.10, 0.0 }, new[] { 0.025, 0.02, 0.025 }),
            (1, _weaponMass, new[] { 0.42 * len, rb - 0.035, 0.0 }, new[] { 0.01, _weaponMass * len * len / 12, _weaponMass * len * len / 12 }),
            (1, 3.0, new[] { 0.20, -0.10, -0.10 }, new[] { 0.03, 0.03, 0.03 })
        };

        double[] zero = new double[N];
        Pose rest = Kinematics(zero);
        double[] cheekWorld = rest.Point(2, Cheek);
        double[] cheekOnGun = rest.Local(1, cheekWorld);
        double[] bore = { 0.25, rb, 0.0 }, carrier = { 0.25, rb, 0.0 }, muzzle = { len, rb, 0.0 };

        const double eps = 1e-6;
        var jv = new double[elements.Length][][];
        var jw = new double[elements.Length][][];
        var jBore = new double[N][];
        var jMuzzle = new double[N][];
        var jCarrier = new double[N][];
        var jCheek = new double[N][];
        for (int e = 0; e < elements.Length; e++)
        {
            jv[e] = new double[N][];
            jw[e] = new double[N][];
        }
        for (int c = 0; c < N; c++)
        {
            double[] q = new double[N];
            q[c] = eps;
            Pose moved = Kinematics(q);
            for (int e = 0; e < elements.Length; e++)
            {
                var el = elements[e];
                jv[e][c] = Scale(Sub(moved.Point(el.body, el.p), rest.Point(el.body, el.p)), 1 / eps);
                jw[e][c] = AngularRate(moved.R[el.body], rest.R[el.body], eps);
            }
            jBore[c] = Scale(Sub(moved.Point(1, bore), rest.Point(1, bore)), 1 / eps);
            jMuzzle[c] = Scale(Sub(moved.Point(1, muzzle), rest.Point(1, muzzle)), 1 / eps);
            jCarrier[c] = Scale(Sub(moved.Point(1, carrier), rest.Point(1, carrier)), 1 / eps);
            jCheek[c] = Scale(Sub(Sub(moved.Point(2, Cheek), moved.Point(1, cheekOnGun)), Sub(rest.Point(2, Cheek), rest.Point(1, cheekOnGun))), 1 / eps);
        }

        double[,] mass = new double[N, N];
        for (int e = 0; e < elements.Length; e++)
        {
            var el = elements[e];
            double[,] r = rest.R[el.body];
            for (int a = 0; a < N; a++)
            for (int b = 0; b < N; b++)
            {
                double v = el.m * Dot(jv[e][a], jv[e][b]);
                double[] wa = MulT(r, jw[e][a]), wb = MulT(r, jw[e][b]);
                v += el.i[0] * wa[0] * wb[0] + el.i[1] * wa[1] * wb[1] + el.i[2] * wa[2] * wb[2];
                mass[a, b] += v;
            }
        }
        for (int a = 0; a < N; a++)
            mass[a, a] += 1e-5;
        Invert(mass, _minv);

        double[] back = { -1.0, 0.0, 0.0 };
        for (int a = 0; a < N; a++)
        {
            _bBore[a] = Dot(jBore[a], back);
            _bMuzzleDown[a] = Dot(jMuzzle[a], new[] { 0.0, -1.0, 0.0 });
            _bCarrier[a] = Dot(jCarrier[a], back);
            for (int b = 0; b < N; b++)
            {
                double g = Dot(jCheek[a], jCheek[b]);
                _k[a, b] = cheekK * g + (a == b ? k[a] : 0.0);
                _d[a, b] = cheekD * g + (a == b ? d[a] : 0.0);
            }
        }
        // The gun's climb from a unit push back along the bore, and from a unit push down at the muzzle, the body left
        // alone: their ratio is how hard a compensator must push down to take a given share of the climb.
        double climbBack = PeakClimb(_bBore), climbDown = PeakClimb(_bMuzzleDown);
        _climbPerDown = climbDown < -1e-9 ? climbBack / -climbDown : 0.0;
        _built = true;
    }

    // The highest gun pitch (waist plus gun) over 0.3 s after a unit impulse through the generalized force b, with no
    // pull back from the shooter. Build time only.
    private static double PeakClimb(double[] b)
    {
        var q = new double[N];
        var qd = new double[N];
        var f = new double[N];
        for (int a = 0; a < N; a++)
        for (int c = 0; c < N; c++)
            qd[a] += _minv[a, c] * b[c];
        double peak = 0.0;
        for (int step = 0; step < (int)(0.3 / Step); step++)
        {
            for (int a = 0; a < N; a++)
            {
                double v = 0.0;
                for (int c = 0; c < N; c++)
                    v -= _k[a, c] * q[c] + _d[a, c] * qd[c];
                f[a] = v;
            }
            for (int a = 0; a < N; a++)
            {
                double acc = 0.0;
                for (int c = 0; c < N; c++)
                    acc += _minv[a, c] * f[c];
                qd[a] += acc * Step;
            }
            for (int a = 0; a < N; a++)
                q[a] += qd[a] * Step;
            double pitch = q[0] + q[3];
            if (Math.Abs(pitch) > Math.Abs(peak))
                peak = pitch;
        }
        return peak;
    }

    private static void Advance(double now)
    {
        if (_pistol)
        {
            AdvancePistol(now);
            return;
        }
        if (!_built)
            return;
        // A long stall (a loading screen, a pause) is not integrated; the body has long settled.
        if (now - _time > 0.5)
            _time = now - 0.5;
        int historySteps = (int)(NerveDelay / Step);
        while (_time + Step <= now)
        {
            _time += Step;
            double t = _time;
            ShotForces(t, out double fBore, out double fCarrier, out double holdPitch, out double holdYaw, out double fDown);

            for (int a = 0; a < N; a++)
            {
                double f = _bBore[a] * fBore + _bCarrier[a] * fCarrier + _bMuzzleDown[a] * fDown;
                for (int b = 0; b < N; b++)
                    f -= _k[a, b] * _q[b] + _d[a, b] * _qd[b];
                _force[a] = f;
            }
            _force[0] += holdPitch;
            _force[1] += holdYaw;

            // The shooter's own pull back to the aim, a nerve delay late, harder as the burst goes on.
            double ePitch = _q[0] + _q[3], eYaw = _q[1] + _q[4];
            double ePitchRate = _qd[0] + _qd[3], eYawRate = _qd[1] + _qd[4];
            _errPitch[_historyHead] = ePitch;
            _errYaw[_historyHead] = eYaw;
            _errPitchRate[_historyHead] = ePitchRate;
            _errYawRate[_historyHead] = eYawRate;
            int late = (_historyHead - historySteps + HistoryLength) % HistoryLength;
            _historyHead = (_historyHead + 1) % HistoryLength;
            _stepCount++;
            if (_stepCount > historySteps)
            {
                double since = t - _lastShot;
                double adapt = _burst == 0 || since > BurstGap ? 0.3 : Math.Min(1.0, 0.3 + 0.7 * (_burst - 1) / Math.Max(1, AdaptShots - 1));
                double gain = adapt * Stamina;
                _force[0] -= gain * _pitchHold * (ControlP * _errPitch[late] + ControlD * _errPitchRate[late]);
                _force[1] -= gain * YawControl * _yawHold * (ControlP * _errYaw[late] + ControlD * _errYawRate[late]);
                _force[3] -= gain * _pitchHold * 0.3 * (ControlP * 0.3 * _errPitch[late] + ControlD * 0.3 * _errPitchRate[late]);
            }

            for (int a = 0; a < N; a++)
            {
                double acc = 0.0;
                for (int b = 0; b < N; b++)
                    acc += _minv[a, b] * _force[b];
                _qdd[a] = acc;
            }
            for (int a = 0; a < N; a++)
            {
                _qd[a] += _qdd[a] * Step;
                _q[a] += _qd[a] * Step;
                if (double.IsNaN(_q[a]) || Math.Abs(_q[a]) > 1.0)
                {
                    Rest();
                    return;
                }
            }
        }
    }

    private static void AdvancePistol(double now)
    {
        if (now - _time > 0.5)
            _time = now - 0.5;
        double wp = 2.0 * Math.PI * PistolPitchHz, wy = 2.0 * Math.PI * PistolYawHz, wb = 2.0 * Math.PI * PistolBackHz;
        double kp = PistolInertia * wp * wp, cp = 2.0 * PistolPitchDamping * PistolInertia * wp;
        double ky = PistolInertia * wy * wy, cy = 2.0 * PistolYawDamping * PistolInertia * wy;
        double kb = PistolArmMass * wb * wb, cb = 2.0 * PistolBackDamping * PistolArmMass * wb;
        while (_time + Step <= now)
        {
            _time += Step;
            ShotForces(_time, out double fBore, out double fCarrier, out double holdPitch, out double holdYaw, out double fDown);
            double back = fBore + fCarrier;
            // The push along the bore, over the wrists, turns the gun up; a compensator takes its share of that turn.
            double torque = PistolBoreOverWrist * (back - fDown) + holdPitch;
            double ap = (torque - kp * _pq[0] - cp * _pqd[0]) / PistolInertia;
            double ay = (holdYaw - ky * _pq[1] - cy * _pqd[1]) / PistolInertia;
            double ab = (back - kb * _pq[2] - cb * _pqd[2]) / PistolArmMass;
            _pqd[0] += ap * Step;
            _pqd[1] += ay * Step;
            _pqd[2] += ab * Step;
            for (int a = 0; a < 3; a++)
            {
                _pq[a] += _pqd[a] * Step;
                if (double.IsNaN(_pq[a]) || Math.Abs(_pq[a]) > 1.0)
                {
                    Rest();
                    return;
                }
            }
        }
    }

    // A half sine of unit area.
    private static double Pulse(double t, double start, double duration)
    {
        if (t < start || t > start + duration)
            return 0.0;
        return Math.PI / (2.0 * duration) * Math.Sin(Math.PI * (t - start) / duration);
    }

    // The push of each live shot along the bore (back +), at the bore and at the carrier (研究 §12, design §2.2).
    private static void ShotForces(double t, out double bore, out double carrier, out double holdPitch, out double holdYaw, out double down)
    {
        down = 0.0;
        bore = 0.0;
        carrier = 0.0;
        holdPitch = 0.0;
        holdYaw = 0.0;
        int count = Math.Min(_shotCount, _shots.Length);
        for (int n = 0; n < count; n++)
        {
            Shot shot = _shots[n];
            double ts = shot.Time, cycle = shot.Cycle, j = shot.Impulse, beta = shot.Beta;
            if (t < ts || t > ts + cycle + 0.01)
                continue;
            double t2 = ts + 0.30 * cycle, t3 = ts + 0.85 * cycle, back = 0.5 * beta * j;
            double gas = Pulse(t, ts, 0.003);
            bore += (1.0 - beta) * j * gas;
            holdPitch += shot.HoldPitch * gas;
            holdYaw += shot.HoldYaw * gas;
            down += shot.MuzzleDown * gas;
            if (beta <= 0.0)
                continue;
            if (t > ts + 0.003 && t < t2)
                carrier += 0.15 * beta * j / (t2 - ts - 0.003);   // the buffer spring as the carrier runs back
            carrier += 0.85 * beta * j * Pulse(t, t2, 0.002);      // the carrier hits the rear
            if (t >= t2 + 0.002 && t < t3)
                carrier += back / (t3 - t2 - 0.002);              // the return spring throws it forward, pushing the gun back
            carrier -= back * Pulse(t, t3, 0.002);                 // and it slams home: a push forward
        }
    }

    // ---- kinematics of the rest model (x forward, y up, z right; pitch about +z, yaw about +y) ----

    private sealed class Pose
    {
        internal readonly double[][,] R = new double[3][,];
        internal readonly double[][] O = new double[3][];

        internal double[] Point(int body, double[] local)
        {
            return Add(O[body], Mul(R[body], local));
        }

        internal double[] Local(int body, double[] world)
        {
            return MulT(R[body], Sub(world, O[body]));
        }
    }

    private static Pose Kinematics(double[] q)
    {
        var pose = new Pose();
        double[,] torso = MatMul(RotY(q[1]), RotZ(q[0]));
        double[,] gun = MatMul(torso, MatMul(RotY(q[4]), RotZ(q[3])));
        double[,] head = MatMul(torso, MatMul(RotY(q[6]), RotZ(q[5])));
        pose.R[0] = torso;
        pose.R[1] = gun;
        pose.R[2] = head;
        pose.O[0] = new double[3];
        pose.O[1] = Add(Mul(torso, Shoulder), Mul(gun, new[] { -q[2], 0.0, 0.0 }));
        pose.O[2] = Mul(torso, Neck);
        return pose;
    }

    private static double[,] RotZ(double a)
    {
        double c = Math.Cos(a), s = Math.Sin(a);
        return new[,] { { c, -s, 0 }, { s, c, 0 }, { 0, 0, 1 } };
    }

    private static double[,] RotY(double a)
    {
        double c = Math.Cos(a), s = Math.Sin(a);
        return new[,] { { c, 0, s }, { 0, 1, 0 }, { -s, 0, c } };
    }

    private static double[,] MatMul(double[,] a, double[,] b)
    {
        var c = new double[3, 3];
        for (int i = 0; i < 3; i++)
        for (int j = 0; j < 3; j++)
            c[i, j] = a[i, 0] * b[0, j] + a[i, 1] * b[1, j] + a[i, 2] * b[2, j];
        return c;
    }

    private static double[] Mul(double[,] a, double[] v)
    {
        return new[]
        {
            a[0, 0] * v[0] + a[0, 1] * v[1] + a[0, 2] * v[2],
            a[1, 0] * v[0] + a[1, 1] * v[1] + a[1, 2] * v[2],
            a[2, 0] * v[0] + a[2, 1] * v[1] + a[2, 2] * v[2]
        };
    }

    private static double[] MulT(double[,] a, double[] v)
    {
        return new[]
        {
            a[0, 0] * v[0] + a[1, 0] * v[1] + a[2, 0] * v[2],
            a[0, 1] * v[0] + a[1, 1] * v[1] + a[2, 1] * v[2],
            a[0, 2] * v[0] + a[1, 2] * v[1] + a[2, 2] * v[2]
        };
    }

    private static double[] AngularRate(double[,] moved, double[,] rest, double eps)
    {
        // R_moved · R_restᵀ ≈ I + [w]× eps
        double[,] a = new double[3, 3];
        for (int i = 0; i < 3; i++)
        for (int j = 0; j < 3; j++)
            a[i, j] = moved[i, 0] * rest[j, 0] + moved[i, 1] * rest[j, 1] + moved[i, 2] * rest[j, 2];
        return new[] { (a[2, 1] - a[1, 2]) / (2 * eps), (a[0, 2] - a[2, 0]) / (2 * eps), (a[1, 0] - a[0, 1]) / (2 * eps) };
    }

    private static double[] Add(double[] a, double[] b) => new[] { a[0] + b[0], a[1] + b[1], a[2] + b[2] };
    private static double[] Sub(double[] a, double[] b) => new[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] };
    private static double[] Scale(double[] a, double s) => new[] { a[0] * s, a[1] * s, a[2] * s };
    private static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];

    private static void Invert(double[,] m, double[,] result)
    {
        int n = m.GetLength(0);
        var a = new double[n, 2 * n];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
                a[i, j] = m[i, j];
            a[i, n + i] = 1.0;
        }
        for (int c = 0; c < n; c++)
        {
            int p = c;
            for (int r = c + 1; r < n; r++)
                if (Math.Abs(a[r, c]) > Math.Abs(a[p, c]))
                    p = r;
            if (p != c)
                for (int k = 0; k < 2 * n; k++)
                    (a[c, k], a[p, k]) = (a[p, k], a[c, k]);
            double d = a[c, c];
            for (int k = 0; k < 2 * n; k++)
                a[c, k] /= d;
            for (int r = 0; r < n; r++)
            {
                if (r == c)
                    continue;
                double f = a[r, c];
                if (f == 0.0)
                    continue;
                for (int k = 0; k < 2 * n; k++)
                    a[r, k] -= f * a[c, k];
            }
        }
        for (int i = 0; i < n; i++)
        for (int j = 0; j < n; j++)
            result[i, j] = a[i, n + j];
    }
}
