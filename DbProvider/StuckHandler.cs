﻿using Buddy.Coroutines;
using System;
using Trinity.Framework;
using Trinity.Framework.Helpers;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Trinity.Components.Adventurer;
using Trinity.Components.Adventurer.Coroutines;
using Trinity.Components.Combat;
using Trinity.Components.Combat.Resources;
using Zeta.Bot;
using Zeta.Bot.Navigation;
using Zeta.Bot.Profile;
using Zeta.Bot.Profile.Common;
using Zeta.Common;
using Zeta.Game;
using Zeta.Game.Internals;
using Zeta.Game.Internals.Actors;
using Zeta.Game.Internals.Actors.Gizmos;
using Trinity.Framework.Reference;
using Trinity.Routines;

namespace Trinity.DbProvider
{
    public class StuckHandler : IStuckHandler
    {
        private DateTime _lastStuckCheck = DateTime.MinValue;
        private Vector3 _lastPosition = Vector3.Zero;
        private bool _isStuck;
        private int _checkIntervalMs = 2000;
        private bool _isSuspectedStuck;
        private int _stuckValidationTime = Core.Player.IsInTown  ? 30000 : 10000;
        private DateTime _suspectedStuckStartTime = DateTime.MaxValue;
        private Vector3 _stuckPosition;
        private Vector3 _suspectedStuckPosition;

        public StuckHandler()
        {
            GameEvents.OnWorldChanged += (sender, args) => Reset();
        }

        public bool IsStuck
        {
            get
            {
                if (DateTime.UtcNow.Subtract(_lastStuckCheck).TotalMilliseconds <= _checkIntervalMs)
                    return _isStuck;

                CheckForStuck();

                if (_isStuck)
                    LastStuckTime = DateTime.UtcNow;

                return _isStuck;
            }
        }

        public DateTime LastStuckTime { get; set; }

        protected bool CheckForStuck()
        {
            _lastPosition = ZetaDia.Me.Position;
            _lastStuckCheck = DateTime.UtcNow;

            if (IsNotStuck())
            {
                if (_isSuspectedStuck)
                    Core.Logger.Log(LogCategory.StuckHandler, "No longer suspected of stuck");

                if (_isStuck)
                    Core.Logger.Log(LogCategory.StuckHandler, "No longer stuck!");

                Reset();
                return _isStuck;
            }

            if (!_isStuck && !_isSuspectedStuck)
            {
                SetSuspectedStuck();
                return _isStuck;
            }

            if (_isSuspectedStuck)
            {
                var millisecondsSuspected = DateTime.UtcNow.Subtract(_suspectedStuckStartTime).TotalMilliseconds;
                if (millisecondsSuspected >= _stuckValidationTime)
                {
                    SetStuck();
                    return _isStuck;
                }

                Core.Logger.Log(LogCategory.StuckHandler, "Suspected Stuck for {0}ms", millisecondsSuspected);
                return _isStuck;
            }

            if (_isStuck)
                return true;

            Reset();
            return _isStuck;
        }

        private void SetStuck()
        {
            Core.Logger.Log(LogCategory.StuckHandler, "Definately Stuck!");
            _isStuck = true;
            _stuckPosition = ZetaDia.Me.Position;
            _isSuspectedStuck = false;
            _suspectedStuckStartTime = DateTime.MaxValue;
        }

        private void SetSuspectedStuck()
        {
            _isStuck = false;
            _isSuspectedStuck = true;
            _suspectedStuckStartTime = DateTime.UtcNow;
            _suspectedStuckPosition = ZetaDia.Me.Position;
        }

        public void Reset(string reason = default(string))
        {
            if (!string.IsNullOrEmpty(reason))
                Core.Logger.Log(LogCategory.StuckHandler, $"Not Stuck: {reason}");

            _isStuck = false;
            _isSuspectedStuck = false;
            _stuckPosition = Vector3.Zero;
            _suspectedStuckStartTime = DateTime.MaxValue;
            _suspectedStuckPosition = Vector3.Zero;
        }

        private bool IsNotStuck()
        {
            if (Core.Settings.Advanced.DisableAllMovement)
            {
                Core.Logger.Log(LogCategory.Movement, $"Not Stuck: Movement is disabled in settings");
                return true;
            }

            if (!ZetaDia.IsInGame || ZetaDia.Me == null || !ZetaDia.Me.IsValid)
            {
                Core.Logger.Log(LogCategory.StuckHandler, $"Not Stuck: Player data invalid");
                return true;
            }

            if (ZetaDia.IsInTown && (UIElements.VendorWindow.IsVisible || UIElements.SalvageWindow.IsVisible) && !ZetaDia.Me.Movement.IsMoving)
                return true;

            if (ZetaDia.Me.IsInConversation || ZetaDia.Globals.IsPlayingCutscene || ZetaDia.Globals.IsLoadingWorld)
            {
                Core.Logger.Log(LogCategory.StuckHandler, $"Not Stuck: Conversation/CutScene/Loading");
                return true;
            }

            if (ZetaDia.Me.LoopingAnimationEndTime > 0)
            {
                Core.Logger.Log(LogCategory.StuckHandler, $"Not Stuck: Casting");
                return true;
            }

            if (_stuckPosition != Vector3.Zero && _stuckPosition.Distance(ZetaDia.Me.Position) > 20f)
            {
                Core.Logger.Log(LogCategory.StuckHandler, $"Not Stuck: Moved {_stuckPosition.Distance(ZetaDia.Me.Position)} from stuck position");
                return true;
            }

            if (_suspectedStuckPosition != Vector3.Zero && _suspectedStuckPosition.Distance(ZetaDia.Me.Position) > 15f)
            {
                Core.Logger.Log(LogCategory.StuckHandler, $"Not Stuck: Moved {_suspectedStuckPosition.Distance(ZetaDia.Me.Position)} from suspected stuck position");
                return true;
            }

            if (TrinityCombat.IsCurrentlyAvoiding)
            {
                Core.Logger.Log(LogCategory.StuckHandler, $"Not Stuck: Currently Avoiding");
                return false;
            }

            if (TrinityCombat.IsCurrentlyKiting)
            {
                Core.Logger.Log(LogCategory.StuckHandler, $"Not Stuck: Currently Kiting");
                return false;
            }

            var isWaiting = TrinityCombat.Targeting.CurrentTarget != null && TrinityCombat.Targeting.CurrentPower?.SNOPower == SNOPower.Walk && Core.Player.MovementSpeed < 4 && Core.Grids.CanRayWalk(ZetaDia.Me.Position, TrinityCombat.Targeting.CurrentTarget.Position);
            if (isWaiting)
            {
                Core.Logger.Log(LogCategory.StuckHandler, $"Not Stuck: Waiting (Routine Walk)");
                return false;
            }

            var secondsSincePowerUse = DateTime.UtcNow.Subtract(SpellHistory.LastSpellUseTime).TotalSeconds;
            if (secondsSincePowerUse < 4 && !_invalidBusyPowers.Contains(SpellHistory.LastPowerUsed) && TrinityCombat.IsInCombat)
            {
                Core.Logger.Log(LogCategory.StuckHandler, $"Not Stuck: Recently cast power in combat ({SpellHistory.LastPowerUsed}, {secondsSincePowerUse}s ago)");
                return true;
            }

            //var startTime = DateTime.UtcNow.Subtract(TrinityPlugin.BotStartTime).TotalSeconds;
            //if (startTime < 10)
            //{
            //    Core.Logger.Log(LogCategory.StuckHandler, $"Not Stuck: Busy Animation State ({startTime})");
            //    return true;
            //}

            var anim = ZetaDia.Me.CommonData.AnimationState;
            if (_busyAnimationStates.Contains(anim))
            {
                Core.Logger.Log(LogCategory.StuckHandler, $"Not Stuck: Busy Animation State ({anim})");
                return true;
            }

            if (Core.Player.MovementSpeed > 3)
            {
                Core.Logger.Log(LogCategory.StuckHandler, $"Not Stuck: Moving (Speed: {Core.Player.MovementSpeed})");
                return true;
            }

            if (ZetaDia.Me.IsDead || UIElements.WaypointMap.IsVisible)
            {
                Core.Logger.Log(LogCategory.StuckHandler, $"Not Stuck: Dead");
                return true;
            }

            if (IsProfileBusy())
            {
                Core.Logger.Log(LogCategory.StuckHandler, $"Not Stuck: Profile is Busy");
                return true;
            }

            if (IsAdventurerBusy())
            {
                Core.Logger.Log(LogCategory.StuckHandler, $"Not Stuck: Adventurer is Busy");
                return true;
            }

            return false;
        }

        private bool IsAdventurerBusy()
        {
            if (Adventurer.GetCurrentCoroutine() is WaitCoroutine)
                return true;

            return false;
        }

        private readonly HashSet<SNOPower> _invalidBusyPowers = new HashSet<SNOPower>
        {
            SNOPower.Walk,
            SNOPower.Consumable_Potion_Buffs,
            SNOPower.DrinkHealthPotion,
            SNOPower.Wizard_Teleport,
            SNOPower.Wizard_Archon_Teleport,
            SNOPower.Wizard_ExplosiveBlast,
            SNOPower.Wizard_FrostNova,
            SNOPower.X1_Crusader_LawsOfHope2,
            SNOPower.X1_Crusader_LawsOfJustice2,
            SNOPower.X1_Crusader_LawsOfValor2,
            SNOPower.X1_Crusader_SteedCharge,
            SNOPower.DemonHunter_ShadowPower,
            SNOPower.DemonHunter_Vault,
            SNOPower.DemonHunter_SmokeScreen,
            SNOPower.X1_DemonHunter_Vengeance,
            SNOPower.Barbarian_Leap,
            SNOPower.Barbarian_ThreateningShout,
            SNOPower.Barbarian_FuriousCharge,
            SNOPower.Barbarian_Whirlwind,
            SNOPower.Barbarian_IgnorePain,
            SNOPower.Monk_BlindingFlash,
            SNOPower.Monk_SweepingWind,
            SNOPower.X1_Monk_MantraOfEvasion_v2,
            SNOPower.X1_Monk_MantraOfConviction_v2,
            SNOPower.X1_Monk_MantraOfHealing_v2,
            SNOPower.X1_Monk_MantraOfRetribution_v2,
            SNOPower.X1_Monk_DashingStrike,
            SNOPower.Monk_BreathOfHeaven,
            SNOPower.X1_Monk_InnerSanctuary,
            SNOPower.Witchdoctor_Horrify,
            SNOPower.Witchdoctor_BigBadVoodoo,
            SNOPower.Witchdoctor_SpiritWalk,
            SNOPower.Witchdoctor_FetishArmy,
            SNOPower.Witchdoctor_Sacrifice,
            SNOPower.Witchdoctor_Firebats,
            SNOPower.Weapon_Ranged_Instant,
            SNOPower.Weapon_Ranged_Projectile,
            SNOPower.Weapon_Ranged_Wand,
        };

        private readonly HashSet<AnimationState> _busyAnimationStates = new HashSet<AnimationState>
        {
            AnimationState.Attacking,
            AnimationState.Channeling,
            AnimationState.Casting,
            AnimationState.Dead,
            AnimationState.Invalid,
        };

        private bool IsProfileBusy()
        {
            ProfileBehavior currentProfileBehavior = null;
            try
            {
                if (ProfileManager.CurrentProfileBehavior != null)
                    currentProfileBehavior = ProfileManager.CurrentProfileBehavior;
            }
            catch (Exception ex)
            {
                Core.Logger.Log("Exception while checking for current profile behavior!");
                Core.Logger.Log(LogCategory.GlobalHandler, ex.ToString());
            }
            if (currentProfileBehavior != null)
            {
                Type profileBehaviortype = currentProfileBehavior.GetType();
                string behaviorName = profileBehaviortype.Name;
                if (profileBehaviortype == typeof(UseTownPortalTag) ||
                     profileBehaviortype == typeof(WaitTimerTag) ||
                     behaviorName.ToLower().Contains("townrun") ||
                     behaviorName.ToLower().Contains("townportal") ||
                     behaviorName.ToLower().Contains("leave") ||
                     behaviorName.ToLower().Contains("wait"))
                {
                    return true;
                }
            }
            return false;
        }

        public async Task<bool> DoUnstick()
        {
            Core.Logger.Warn("Trying to get Unstuck...");
            Navigator.Clear();
            Navigator.NavigationProvider.Clear();
            await ClearDestructibleNearby();

            await Navigator.SearchGridProvider.Update();
            var startPosition = ZetaDia.Me.Position;
            Core.Logger.Log("Starting Segment 1...");
            await MoveAwayFrom(startPosition);
            Core.Logger.Log("Starting Segment 2 ...");
            await MoveAwayFrom(startPosition);
            return true;
        }

        private async Task<bool> ClearDestructibleNearby()
        {
            await Coroutine.Sleep(250);

            var target = ZetaDia.Actors.GetActorsOfType<DiaGizmo>(true)
                .Where(a => GameData.DestructibleTypes.Contains(a.ActorInfo.GizmoType))
                .OrderBy(a => a.Distance).FirstOrDefault();

            if (target == null || !target.IsFullyValid())
                return false;

            var pos = target.Position;
            var acdId = target.CommonData?.ACDId ?? -1;
            var skill = SkillUtils.Active.FirstOrDefault(s => s.IsDamaging);

            await Coroutine.Sleep(250);

            if (Core.IsOutOfGame)
                return false;

            TrinityCombat.Spells.CastPower(skill?.SNOPower ?? RoutineBase.DefaultWeaponPower, pos, acdId);

            await Coroutine.Sleep(250);
            return true;

        }

        private static async Task<bool> MoveAwayFrom(Vector3 position)
        {
            var startTime = DateTime.UtcNow;

            var positions = GenerateUnstickPositions(position);

            if (!positions.Any())
            {
                Core.Logger.Debug($"No Unstuck Positions were found. IsMainGridProviderEmpty={Core.DBGridProvider.Width == 0}");
                return false;
            }

            var targetPosition = positions.First();
            var segmentStartTime = DateTime.UtcNow;

            while (DateTime.UtcNow.Subtract(startTime).TotalSeconds < 6)
            {
                if (targetPosition == Vector3.Zero)
                    break;

                var distance = targetPosition.Distance(ZetaDia.Me.Position);

                Core.Logger.Log("Moving to {0} Dist={1}", targetPosition, distance);

                if (Core.IsOutOfGame)
                    return false;

                ZetaDia.Me.UsePower(SNOPower.Walk, targetPosition, ZetaDia.Globals.WorldId);

                await Coroutine.Sleep(50);

                if (distance < 4f || position.Distance(ZetaDia.Me.Position) > 25f)
                    break;

                if (!ZetaDia.Me.Movement.IsMoving || DateTime.UtcNow.Subtract(segmentStartTime).TotalMilliseconds > 4500)
                {
                    positions.Remove(targetPosition);
                    segmentStartTime = DateTime.UtcNow;
                    targetPosition = positions.FirstOrDefault();
                }
            }
            return true;
        }

        private static List<Vector3> GenerateUnstickPositions(Vector3 avoidPosition)
        {
            var myPosition = ZetaDia.Me.Position;
            var distanceToAwayPosition = avoidPosition.Distance(ZetaDia.Me.Position);
            var circlePoints = GetCirclePoints(10, 65, ZetaDia.Me.Position);
            if (!circlePoints.Any())
            {
                Core.Logger.Debug("No circle unstick positions found!");
                circlePoints = GetCirclePoints(10, 150, ZetaDia.Me.Position);
            }
            var directionalPoints = circlePoints.Where(p => p.Distance(myPosition) > distanceToAwayPosition);
            if (!directionalPoints.Any())
            {
                Core.Logger.Debug("No directional unstick positions found!");
                directionalPoints = circlePoints;
            }
            var validatedPoints = directionalPoints.Where(p => Core.DBGridProvider.CanStandAt(p));
            if (!validatedPoints.Any())
            {
                Core.Logger.Debug("No validated unstick positions found!");
                validatedPoints = directionalPoints;
            }
            return RandomShuffle(validatedPoints.ToList());
        }

        public static List<T> RandomShuffle<T>(List<T> list)
        {
            var rng = new Random();
            var n = list.Count;
            while (n > 1)
            {
                n--;
                var k = rng.Next(n + 1);
                var value = list[k];
                list[k] = list[n];
                list[n] = value;
            }
            return list;
        }

        public static List<Vector3> GetCirclePoints(int points, double radius, Vector3 center)
        {
            var result = new List<Vector3>();
            var slice = 2 * Math.PI / points;
            for (var i = 0; i < points; i++)
            {
                var angle = slice * i;
                var newX = (int)(center.X + radius * Math.Cos(angle));
                var newY = (int)(center.Y + radius * Math.Sin(angle));

                var newpoint = new Vector3(newX, newY, center.Z);
                result.Add(newpoint);

                //Core.Logger.Debug(LogCategory.Movement, "Calculated point {0}: {1}", i, newpoint.ToString());
            }
            return result;
        }
    }
}