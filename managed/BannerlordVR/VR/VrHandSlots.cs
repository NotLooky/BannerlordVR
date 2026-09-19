using System;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.MountAndBlade;

namespace BannerlordVR.VR
{
    /// <summary>The character's two hands. NOT the player's - see VrHand.</summary>
    public enum HandRole
    {
        /// <summary>Where the weapon goes. r_hand on a human.</summary>
        Main = 0,

        /// <summary>Where the shield goes. l_hand on a human.</summary>
        Off = 1,
    }

    /// <summary>The player's two controllers.</summary>
    public enum VrHand
    {
        Left = 0,
        Right = 1,
    }

    /// <summary>One hand bone and the controller that drives it.</summary>
    public struct VrHandSlot
    {
        public bool Valid;
        public HandRole Role;
        public sbyte Bone;
        public sbyte Parent;
        public VrHand Controller;

        /// <summary>Which equipment slot the item in this hand comes from. The
        /// weapon lives on its own GameEntity, and this is how it is fetched -
        /// see VrWeaponEntity.</summary>
        public EquipmentIndex Equipment;
    }

    /// <summary>
    /// Decides which bone each controller drives.
    ///
    /// TWO SEPARATE QUESTIONS, PREVIOUSLY CONFLATED
    ///
    /// "Which bone holds the sword" and "which of the player's hands should move
    /// it" are not the same question, and the old code could only answer them
    /// together because it drove exactly one bone named in the config. That is
    /// fine while the answer is always "the right one", and wrong the moment a
    /// bow is involved.
    ///
    /// WHERE THE ITEMS ACTUALLY HANG - from the game's own data, not a guess
    ///
    /// Native/ModuleData/monsters.xml, the human monster:
    ///
    ///     main_hand_bone       = r_hand      off_hand_bone       = l_hand
    ///     main_hand_item_bone  = r_finger0   off_hand_item_bone  = l_finger0
    ///
    /// So weapons are parented to r_finger0 and shields to l_finger0, and both
    /// are CHILDREN of the hand bones. Posing a hand carries its item along, which
    /// is what makes this approach work at all. Those four names come back through
    /// Monster.MainHandBoneIndex and friends, so this is read rather than matched:
    /// a bone index from the game's own monster definition cannot pick the wrong
    /// hand the way a substring search can, and it works for any skeleton.
    ///
    /// The name search is kept as a fallback for the case where a Monster does not
    /// declare them - see VrWeaponHands.
    ///
    /// WHICH CONTROLLER, AND THE ONE CASE THAT SWAPS
    ///
    /// Bannerlord's main hand is the character's RIGHT, so the natural mapping is
    /// main-to-right and off-to-left. That already satisfies most of what was
    /// asked: a one-handed sword, a two-handed sword and a polearm are all main
    /// hand items and land on the right controller; a shield is an off hand item
    /// and lands on the left.
    ///
    /// A bow is the exception, and not because of anything we decide. A bow is a
    /// MAIN HAND item - it hangs off r_finger0 like a sword does - but the hand
    /// that holds a bow steady is the one the player thinks of as their bow hand,
    /// and it was asked for on the left. The string is held by the other hand;
    /// that is what WeaponFlags.StringHeldByHand means, and it is what
    /// WeaponComponentData.IsBow tests for.
    ///
    /// So for bows and crossbows the two controllers trade places: the bone
    /// holding the bow is driven by the LEFT controller and the string hand by the
    /// right. Nothing about the skeleton changes - only which of the player's
    /// hands is asked about each bone. That is the whole of the special case, and
    /// it is why bone and controller are separate fields on a slot.
    /// </summary>
    public static class VrHandSlots
    {
        /// <summary>Whether the empty hand tracks too, or only the one holding
        /// something. Both, by default: a hand that stops existing when you sheathe
        /// a sword is more startling than one with nothing in it.</summary>
        private static readonly bool FollowBoth = VrConfig.Bool("hand_follow_both", true);

        /// <summary>Whether a bow trades the controllers over. Config rather than
        /// a constant because it is a claim about how the player wants to hold a
        /// bow, and that is not ours to be certain about.</summary>
        private static readonly bool SwapForBows = VrConfig.Bool("hand_swap_for_bows", true);

        /// <summary>What a hand bone is called, for the fallback search. Only used
        /// when the monster definition names no hand bones of its own.</summary>
        private static readonly string BoneName =
            VrConfig.String("hand_bone_name", "hand").Trim().ToLowerInvariant();

        private static bool _loggedBones;
        private static string _loggedHeld;

        /// <summary>
        /// Fills in the two slots for this frame. Returns false if neither hand
        /// could be resolved, in which case both slots are left invalid.
        /// </summary>
        public static bool Resolve(Agent agent, Skeleton skeleton,
                                   out VrHandSlot main, out VrHandSlot off)
        {
            main = default(VrHandSlot);
            off = default(VrHandSlot);

            sbyte mainBone, offBone;
            if (!Bones(agent, skeleton, out mainBone, out offBone))
                return false;

            // Read once. Both of these go through the interop layer and then index
            // the agent's equipment, and everything below asks about them two or
            // three times - at frame rate, for an answer that cannot change within
            // a frame.
            WeaponComponentData held = Held(agent.WieldedWeapon);
            WeaponComponentData offHeld = Held(agent.WieldedOffhandWeapon);

            // Which of the character's hands is on which side. Read off the bone
            // NAME rather than assumed, because the swap below is expressed in the
            // player's terms ("the bow goes in my left hand") and translating that
            // into a bone needs to know which way round the character is.
            bool mainIsRight = SideOf(skeleton, mainBone, expectRight: true);

            // Whether what is being held trades the controllers over.
            bool swap = SwapForBows && held != null && (held.IsBow || held.IsCrossBow);

            VrHand mainSide = mainIsRight ? VrHand.Right : VrHand.Left;
            VrHand offSide = mainIsRight ? VrHand.Left : VrHand.Right;

            main = Slot(skeleton, HandRole.Main, mainBone, swap ? offSide : mainSide);
            off = Slot(skeleton, HandRole.Off, offBone, swap ? mainSide : offSide);

            // The wielded slots, so the weapon entity can be fetched. WieldedItem
            // rather than a fixed index: which of the four weapon slots is in hand
            // changes every time the player switches, and the entity belongs to the
            // slot rather than to the hand.
            main.Equipment = agent.GetPrimaryWieldedItemIndex();
            off.Equipment = agent.GetOffhandWieldedItemIndex();

            if (!FollowBoth)
            {
                // Only the hand with something in it. The main hand wins when both
                // are full, because that is the one with the weapon.
                if (held != null)
                    off.Valid = false;
                else if (offHeld != null)
                    main.Valid = false;
            }

            Describe(held, offHeld, main, off);
            return main.Valid || off.Valid;
        }

        /// <summary>
        /// The two hand bones, from the monster definition.
        ///
        /// Monster.MainHandBoneIndex is the game's own answer to "which bone is the
        /// weapon hand", loaded from monsters.xml alongside the animations that
        /// pose it. Searching the bone table for a name that looks like a hand is
        /// a reconstruction of a fact that is sitting right there.
        /// </summary>
        private static bool Bones(Agent agent, Skeleton skeleton, out sbyte main, out sbyte off)
        {
            main = -1;
            off = -1;

            int count = skeleton.GetBoneCount();
            Monster monster = agent.Monster;

            if (monster != null)
            {
                main = Valid(monster.MainHandBoneIndex, count) ? monster.MainHandBoneIndex : (sbyte)(-1);
                off = Valid(monster.OffHandBoneIndex, count) ? monster.OffHandBoneIndex : (sbyte)(-1);
            }

            if (main >= 0 || off >= 0)
            {
                if (!_loggedBones)
                {
                    _loggedBones = true;
                    VrLog.Info("Hand bones from the monster definition: main = "
                               + Name(skeleton, main) + ", off = " + Name(skeleton, off)
                               + ". Items hang off their child bones, so posing these carries "
                               + "the weapon and the shield with them.");
                }

                return true;
            }

            // Nothing declared. Fall back to the bone table, which is where this
            // started - see VrBoneNames for why the side test is not a substring.
            // Bannerlord's own convention is main-hand-is-right, so that is which
            // way round the two names are read.
            main = FindByName(skeleton, count, "r");
            off = FindByName(skeleton, count, "l");

            if (!_loggedBones)
            {
                _loggedBones = true;

                if (main >= 0 || off >= 0)
                {
                    VrLog.Warn("This monster declares no hand bones, so they were found by name "
                               + "instead: main = " + Name(skeleton, main) + ", off = "
                               + Name(skeleton, off) + ". Check those against the bone table "
                               + "above; hand_bone_name selects a different word to look for.");
                }
                else
                {
                    VrLog.Warn("No hand bones: the monster declares none and nothing in the bone "
                               + "table matches '" + BoneName + "', so the hands cannot follow "
                               + "the controllers. Set hand_bone_name to a name from the table "
                               + "logged above.");
                }
            }

            return main >= 0 || off >= 0;
        }

        /// <summary>
        /// The first bone whose name looks like a hand on the given side.
        ///
        /// Deliberately exact about the side rather than falling back to "the first
        /// hand-ish bone I saw". That fallback is what put the sword in the left
        /// hand last time: with l_hand and r_hand both matching 'hand', taking the
        /// first one is a coin flip that always lands the same way.
        /// </summary>
        private static sbyte FindByName(Skeleton skeleton, int count, string side)
        {
            for (sbyte i = 0; i < count && i >= 0; i++)
            {
                string name = (skeleton.GetBoneName(i) ?? string.Empty).ToLowerInvariant();

                if (name.Length != 0 && name.Contains(BoneName) && VrBoneNames.MatchesSide(name, side))
                    return i;
            }

            return -1;
        }

        private static VrHandSlot Slot(Skeleton skeleton, HandRole role, sbyte bone, VrHand controller)
        {
            var slot = default(VrHandSlot);

            if (bone < 0)
                return slot;

            sbyte parent = skeleton.GetParentBoneIndex(bone);
            if (parent < 0)
                return slot;      // a hand with no forearm is not a skeleton we know

            slot.Valid = true;
            slot.Role = role;
            slot.Bone = bone;
            slot.Parent = parent;
            slot.Controller = controller;
            return slot;
        }

        /// <summary>
        /// What is actually in a slot, or null.
        ///
        /// The bow test above is asked of the ITEM rather than of the animation,
        /// because the item is what carries the flag: IsBow is StringHeldByHand
        /// plus AutoReload, both set in the item's own definition. A thrown weapon
        /// or a sling is not a bow by that test and does not swap, which is right -
        /// you throw with the hand that holds the thing.
        /// </summary>
        private static WeaponComponentData Held(MissionWeapon weapon)
        {
            return weapon.IsEmpty ? null : weapon.CurrentUsageItem;
        }

        /// <summary>
        /// Whether a bone is on the character's right, by name.
        ///
        /// Falls back to the caller's expectation rather than to a coin flip: on a
        /// human this is r_hand and the answer is yes, and a skeleton that names
        /// its bones in some other way is better served by Bannerlord's own
        /// main-hand-is-the-right-hand convention than by whatever a failed string
        /// match would have returned.
        /// </summary>
        private static bool SideOf(Skeleton skeleton, sbyte bone, bool expectRight)
        {
            if (bone < 0)
                return expectRight;

            string name = (skeleton.GetBoneName(bone) ?? string.Empty).ToLowerInvariant();
            if (name.Length == 0)
                return expectRight;

            bool right = VrBoneNames.MatchesSide(name, "r");
            bool left = VrBoneNames.MatchesSide(name, "l");

            // Both or neither means the name did not settle it.
            if (right == left)
                return expectRight;

            return right;
        }

        /// <summary>
        /// Says once, and again whenever it changes, what is in each hand and which
        /// controller is driving it. This is the line that answers "why is my sword
        /// following the wrong hand" without another play session.
        /// </summary>
        private static void Describe(WeaponComponentData held, WeaponComponentData offHeld,
                                     VrHandSlot main, VrHandSlot off)
        {
            string mainText = Classify(held);
            string offText = Classify(offHeld);

            // Keyed on the controllers too, not just the items. Swapping for a bow
            // changes which hand drives which bone without changing what is held,
            // and that is precisely the line worth seeing in the log.
            string state = mainText + "/" + offText + "/" + Side(main) + "/" + Side(off);

            if (state == _loggedHeld)
                return;

            _loggedHeld = state;

            VrLog.Info("Hands: main holds " + mainText + " (driven by the " + Side(main)
                       + " controller), off holds " + offText + " (driven by the "
                       + Side(off) + " controller).");
        }

        private static string Side(VrHandSlot slot)
        {
            if (!slot.Valid)
                return "no";

            return slot.Controller == VrHand.Left ? "left" : "right";
        }

        private static string Classify(WeaponComponentData weapon)
        {
            if (weapon == null)
                return "nothing";
            if (weapon.IsShield)
                return "a shield";
            if (weapon.IsBow)
                return "a bow";
            if (weapon.IsCrossBow)
                return "a crossbow";
            if (weapon.IsPolearm)
                return "a polearm";
            if (weapon.IsTwoHanded)
                return "a two-handed weapon";
            if (weapon.IsMeleeWeapon)
                return "a one-handed weapon";
            if (weapon.IsRangedWeapon)
                return "a thrown weapon";

            return weapon.WeaponClass.ToString();
        }

        private static bool Valid(sbyte bone, int count)
        {
            return bone >= 0 && bone < count;
        }

        private static string Name(Skeleton skeleton, sbyte bone)
        {
            if (bone < 0)
                return "(none)";

            return (skeleton.GetBoneName(bone) ?? "?") + " [" + bone + "]";
        }

        public static void Reset()
        {
            _loggedBones = false;
            _loggedHeld = null;
        }
    }
}
