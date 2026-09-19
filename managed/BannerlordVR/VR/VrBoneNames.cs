namespace BannerlordVR.VR
{
    /// <summary>
    /// Reading a side off a bone name.
    ///
    /// WHY THIS EXISTS AT ALL
    ///
    /// It should not have to. Monster.MainHandBoneIndex and OffHandBoneIndex are
    /// the game's own answer to which bone is which hand, and VrHandSlots asks
    /// them first. But two things still need a name:
    ///
    ///   - deciding whether the main hand is the character's LEFT or RIGHT, which
    ///     is what turns "the bow goes in my left hand" into a bone index;
    ///   - the fallback search, for a Monster that declares no hand bones.
    ///
    /// WHY A PLAIN Contains WAS NOT ENOUGH
    ///
    /// The config shipped weapon_bone_side = _r, written against a guessed bone
    /// table of 'hand_R'. The real skeleton names its bones the other way round -
    /// 'l_hand' and 'r_hand' - and "r_hand".Contains("_r") is false. So the side
    /// never matched, the fallback took the first hand in the table, and that is
    /// the left one. Two naming conventions, one substring test, and the weapon
    /// ended up in the wrong hand.
    ///
    /// Rather than swapping one convention's guess for the other's, this takes the
    /// side as a SIDE - the letter, however it happens to be attached. 'r_hand',
    /// 'hand_r', 'hand.R', 'bip01_r_hand' and 'Hand R' all name the right hand,
    /// and all of them answer true here.
    /// </summary>
    public static class VrBoneNames
    {
        /// <summary>Whether a bone name is on the requested side. The name is
        /// expected lower case; the side may be a bare letter or an anchored token
        /// such as '_r'.</summary>
        public static bool MatchesSide(string name, string side)
        {
            if (string.IsNullOrEmpty(side))
                return true;

            if (string.IsNullOrEmpty(name))
                return false;

            // Taken literally ONLY when the configured value carries a separator of
            // its own - '_r', 'r_', 'hand.r'. Those are already anchored, so
            // honouring them exactly keeps an explicit, working config behaving the
            // way it reads.
            //
            // A bare letter gets no such shortcut, and the test table is why: a
            // plain Contains on side 'r' matches 'l_forearm_twist', which is not a
            // hand at all and is on the wrong side twice over. One character is not
            // enough information to be taken literally.
            if (IsAnchored(side) && name.Contains(side))
                return true;

            // Otherwise reduce the config value to the side letter itself and look
            // for it as a token: 'r' at a boundary, not 'r' inside a word. The word
            // test is what keeps 'forearm' and 'r_hand' apart.
            string token = side.Trim('_', '.', '-', ' ');
            if (token.Length == 0)
                return false;

            for (int i = 0; i + token.Length <= name.Length; i++)
            {
                if (string.CompareOrdinal(name, i, token, 0, token.Length) != 0)
                    continue;

                bool startsClean = i == 0 || !IsWordChar(name[i - 1]);
                int after = i + token.Length;
                bool endsClean = after == name.Length || !IsWordChar(name[after]);

                if (startsClean && endsClean)
                    return true;
            }

            return false;
        }

        private static bool IsWordChar(char c)
        {
            return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9');
        }

        /// <summary>Whether a side token already carries its own boundary, and so
        /// can be trusted as a literal substring.</summary>
        private static bool IsAnchored(string side)
        {
            for (int i = 0; i < side.Length; i++)
            {
                if (!IsWordChar(side[i]))
                    return true;
            }

            return false;
        }
    }
}
