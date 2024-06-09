using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;

namespace Nova
{
    public static class TextProxyTypesetter
    {
        private static readonly HashSet<char> ChineseOpeningPunctuations = new HashSet<char>("‘“（【《");
        private static readonly HashSet<char> ChineseClosingPunctuations = new HashSet<char>("，。、；：？！’”）】》");
        private static readonly HashSet<char> ChineseMiddlePunctuations = new HashSet<char>("….·—⸺");

        private static readonly HashSet<char> ChineseFollowingPunctuations =
            new HashSet<char>(ChineseClosingPunctuations.Concat(ChineseMiddlePunctuations));

        private static readonly HashSet<char> EnglishChars = new HashSet<char>(
            "0123456789" +
            "ABCDEFGHIJKLMNOPQRSTUVWXYZ" +
            "abcdefghijklmnopqrstuvwxyz" +
            " ([{<)]}>"
        );

        private static bool IsChineseCharacter(char c)
        {
            return c >= 0x4e00 && c <= 0x9fff;
        }

        private static bool IsLeftAligned(int alignment)
        {
            return (alignment & ((int)HorizontalAlignmentOptions.Left | (int)HorizontalAlignmentOptions.Justified)) > 0;
        }

        private const float ChinesePunctuationKerning = -0.5f;
        private const float ChinesePunctuationSubKerning = -0.3333f;

        // The values are used as tags and will be replaced by actual widths
        private const float FlexibleKerning = -1f;
        private const float FlexibleSubKerning = -2f;

        private static List<float> GetCanAbsorbWidths(TMP_Text textBox, TMP_TextInfo textInfo, int lineIdx)
        {
            var widths = new List<float>();
            if (lineIdx >= textInfo.lineCount)
            {
                return widths;
            }

            var lineInfo = textInfo.lineInfo[lineIdx];
            if (lineInfo.characterCount < 1)
            {
                return widths;
            }

            var characterInfos = textInfo.characterInfo;
            var charIdx = lineInfo.firstCharacterIndex;
            var charInfo = characterInfos[charIdx];
            var origin = charInfo.origin;
            var character = charInfo.character;
            // 0: Chinese character; 1: Chinese following punctuation
            if (IsChineseCharacter(character))
            {
                // Pass
            }
            else if (ChineseFollowingPunctuations.Contains(character))
            {
                // Pass
            }
            else
            {
                return widths;
            }

            var advance = charInfo.xAdvance - origin;
            ++charIdx;

            var lastCharIdx = lineInfo.lastCharacterIndex;
            var fontSize = textBox.fontSize;
            while (charIdx <= lastCharIdx)
            {
                charInfo = characterInfos[charIdx];
                character = charInfo.character;
                if (IsChineseCharacter(character))
                {
                    widths.Add(advance / fontSize);
                }
                else if (ChineseFollowingPunctuations.Contains(character))
                {
                    // Pass
                }
                else
                {
                    break;
                }

                advance = charInfo.xAdvance - origin;
                ++charIdx;
            }

            widths.Add(advance / fontSize);
            return widths;
        }

        private static void ScanKern(TMP_CharacterInfo[] characterInfos, TMP_LineInfo lineInfo,
            List<int> idxs, List<float> kerns,
            out float kernSum, out int flexibleCount, out int flexibleSubCount, out int englishCount)
        {
            idxs.Clear();
            kerns.Clear();
            kernSum = 0f;
            flexibleCount = 0;
            flexibleSubCount = 0;
            englishCount = 0;

            if (lineInfo.characterCount < 2)
            {
                return;
            }

            var firstCharIdx = lineInfo.firstCharacterIndex;
            var lastCharIdx = lineInfo.lastCharacterIndex;

            // From right to left
            var charIdx = lastCharIdx;
            var leftIdx = characterInfos[charIdx].index;
            var leftChar = characterInfos[charIdx].character;
            var leftOpen = ChineseOpeningPunctuations.Contains(leftChar);
            var leftClose = ChineseClosingPunctuations.Contains(leftChar);
            var leftChinese = IsChineseCharacter(leftChar);
            var leftEnglish = EnglishChars.Contains(leftChar);
            englishCount += leftEnglish ? 1 : 0;
            while (charIdx > firstCharIdx)
            {
                var rightIdx = leftIdx;
                var rightOpen = leftOpen;
                var rightClose = leftClose;
                var rightChinese = leftChinese;
                var rightEnglish = leftEnglish;
                --charIdx;
                leftIdx = characterInfos[charIdx].index;
                leftChar = characterInfos[charIdx].character;
                leftOpen = ChineseOpeningPunctuations.Contains(leftChar);
                leftClose = ChineseClosingPunctuations.Contains(leftChar);
                leftChinese = IsChineseCharacter(leftChar);
                leftEnglish = EnglishChars.Contains(leftChar);
                englishCount += leftEnglish ? 1 : 0;
                if (leftClose && rightOpen)
                {
                    idxs.Add(rightIdx);
                    kerns.Add(ChinesePunctuationKerning);
                    kernSum += ChinesePunctuationKerning;
                }
                else if ((leftOpen && rightOpen) || (leftClose && rightClose))
                {
                    idxs.Add(rightIdx);
                    kerns.Add(ChinesePunctuationSubKerning);
                    kernSum += ChinesePunctuationSubKerning;
                }
                else if ((leftChinese && rightOpen) || (leftClose && rightChinese) ||
                         (leftEnglish && rightOpen) || (leftClose && rightEnglish))
                {
                    idxs.Add(rightIdx);
                    kerns.Add(FlexibleKerning);
                    ++flexibleCount;
                }
                else if ((leftEnglish && rightClose) || (leftOpen && rightEnglish) ||
                         (leftChinese && rightEnglish) || (leftEnglish && rightChinese))
                {
                    idxs.Add(rightIdx);
                    kerns.Add(FlexibleSubKerning);
                    ++flexibleSubCount;
                }
            }

            if (leftOpen && IsLeftAligned((int)lineInfo.alignment))
            {
                idxs.Add(leftIdx);
                kerns.Add(ChinesePunctuationKerning);
            }
        }

        // Round toward zero
        private static float RoundKern(float kern)
        {
            return Mathf.Sign(kern) * Mathf.Floor(Mathf.Abs(kern) * 1e4f) / 1e4f;
        }

        private static void GetFlexibleWidth(TMP_Text textBox, RectTransform rectTransform,
            TMP_CharacterInfo[] characterInfos, TMP_LineInfo lineInfo,
            float kernSum, int flexibleCount, int flexibleSubCount, List<float> canAbsorbWidths,
            out float flexibleWidth, out float flexibleSubWidth, out float endMargin,
            out bool needFlush, out bool canAvoidOrphan)
        {
            const float subRatio = 0.5f;

            flexibleWidth = 0f;
            flexibleSubWidth = 0f;
            endMargin = 0f;
            needFlush = false;
            canAvoidOrphan = true;

            if (flexibleCount <= 0 && flexibleSubCount <= 0)
            {
                return;
            }

            if (characterInfos[lineInfo.lastCharacterIndex].character == '\n')
            {
                // No need to add flexible width when end with hard line break
                return;
            }

            // lineInfo.length is not compressed by justified alignment, and it can be larger than rectTransform.rect.width
            // lineInfo.width is compressed
            var totalFlexibleWidth = (rectTransform.rect.width - (lineInfo.length + kernSum)) / textBox.fontSize;
            // var oldTotalFlexibleWidth = totalFlexibleWidth;

            needFlush = true;
            // Absorb characters from the next line
            for (var i = 0; i < canAbsorbWidths.Count; ++i)
            {
                if (totalFlexibleWidth >= canAbsorbWidths[i] &&
                    (i == canAbsorbWidths.Count - 1 || totalFlexibleWidth < canAbsorbWidths[i + 1]))
                {
                    totalFlexibleWidth -= canAbsorbWidths[i];
                    needFlush = false;
                    break;
                }
            }

            // Evenly distribute flexible width, and add 1 for character spacing
            float totalFlexibleCount = flexibleCount + subRatio * flexibleSubCount + 1;
            flexibleWidth = totalFlexibleWidth / totalFlexibleCount;
            flexibleSubWidth = subRatio * flexibleWidth;

            flexibleWidth = RoundKern(Mathf.Clamp(flexibleWidth, -0.3333f, 0.5f));
            flexibleSubWidth = RoundKern(Mathf.Clamp(flexibleSubWidth, 0f, 0.5f));

            var newTotalFlexibleWidth = (flexibleCount + 1) * flexibleWidth + flexibleSubCount * flexibleSubWidth;
            endMargin = totalFlexibleWidth - newTotalFlexibleWidth;

            // Debug.Log(
            //     $"{characterInfos[lineInfo.firstVisibleCharacterIndex].character} {characterInfos[lineInfo.lastVisibleCharacterIndex].character} " +
            //     $"{oldTotalFlexibleWidth} {totalFlexibleWidth} | {flexibleCount} {flexibleWidth} {flexibleSubCount} {flexibleSubWidth} | {endMargin} " +
            //     $"{Utils.GetPath(textBox)}"
            // );

            if (endMargin < 0.01f)
            {
                endMargin = 0f;
            }
            else
            {
                endMargin = RoundKern(endMargin);
            }

            if ((totalFlexibleWidth + 1f) / totalFlexibleCount > 1f)
            {
                // Too much width
                canAvoidOrphan = false;
            }
        }

        private static readonly Dictionary<float, string> KernStrCache = new Dictionary<float, string>();

        private static string GetKernStr(float kern)
        {
            if (KernStrCache.TryGetValue(kern, out var str))
            {
                return str;
            }

            str = $"<space={kern:F4}em>";
            KernStrCache[kern] = str;
            return str;
        }

        // Add a negative space between each punctuation pair,
        // and before each opening punctuation at line beginning if left aligned
        private static void ApplyKerningLine(TMP_Text textBox, RectTransform rectTransform,
            ref string text, ref TMP_TextInfo textInfo, int lineIdx,
            List<int> idxs, List<float> kerns, List<float> canAbsorbWidths,
            out bool canAvoidOrphan)
        {
            var characterInfos = textInfo.characterInfo;
            var lineInfo = textInfo.lineInfo[lineIdx];
            ScanKern(characterInfos, lineInfo, idxs, kerns,
                out var kernSum, out var flexibleCount, out var flexibleSubCount, out var englishCount);

            var flexibleWidth = 0f;
            var flexibleSubWidth = 0f;
            var endMargin = 0f;
            var needFlush = false;
            canAvoidOrphan = false;
            if (lineIdx < textInfo.lineCount - 1)
            {
                GetFlexibleWidth(textBox, rectTransform, characterInfos, lineInfo,
                    kernSum, flexibleCount, flexibleSubCount, canAbsorbWidths,
                    out flexibleWidth, out flexibleSubWidth, out endMargin, out needFlush, out canAvoidOrphan);
            }

            var dirty = false;
            needFlush &= IsLeftAligned((int)lineInfo.alignment);
            if (needFlush)
            {
                var lastIdx = characterInfos[lineInfo.lastCharacterIndex].index;
                if (text[lastIdx] == '\v')
                {
                    text = text.Insert(lastIdx, "</align>");
                }
                else
                {
                    lastIdx += 1;
                    text = text.Insert(lastIdx, "</align>\v");
                }

                // Exact float equal
                if (endMargin != 0f)
                {
                    idxs.Insert(0, lastIdx);
                    kerns.Insert(0, endMargin);
                    // Prevent ignoring kern at line end
                    text = text.Insert(lastIdx, "\u00A0");
                }

                dirty = true;
            }

            for (var i = 0; i < idxs.Count; ++i)
            {
                var kern = kerns[i];
                // Exact float equal
                if (kern == FlexibleKerning)
                {
                    kern = flexibleWidth;
                }
                else if (kern == FlexibleSubKerning)
                {
                    kern = flexibleSubWidth;
                }

                // Exact float equal
                if (kern != 0f)
                {
                    text = text.Insert(idxs[i], GetKernStr(kern));
                    dirty = true;
                }
            }

            if (needFlush)
            {
                var firstIdx = characterInfos[lineInfo.firstCharacterIndex].index;
                text = text.Insert(firstIdx, "<align=\"flush\">");
            }

            if (dirty)
            {
                textInfo = textBox.GetTextInfo(text);
            }
        }

        // If the last line is one Chinese character and some (or zero) Chinese punctuations,
        // and the second last line's last character is Chinese character,
        // then add a line break before the second last line's last character
        private static bool PeekAvoidOrphan(TMP_TextInfo textInfo, int lineIdx)
        {
            if (lineIdx >= textInfo.lineCount - 1)
            {
                return false;
            }

            var nextLineInfo = textInfo.lineInfo[lineIdx + 1];
            if (nextLineInfo.characterCount < 1)
            {
                return false;
            }

            var characterInfos = textInfo.characterInfo;
            if (!IsChineseCharacter(characterInfos[nextLineInfo.firstCharacterIndex].character))
            {
                return false;
            }

            if (lineIdx < textInfo.lineCount - 2)
            {
                if (characterInfos[nextLineInfo.lastCharacterIndex].character != '\n')
                {
                    return false;
                }
            }

            var nextLineLastCharIdx = nextLineInfo.lastCharacterIndex;
            for (var charIdx = nextLineInfo.firstCharacterIndex + 1; charIdx <= nextLineLastCharIdx; ++charIdx)
            {
                if (!ChineseFollowingPunctuations.Contains(characterInfos[charIdx].character))
                {
                    return false;
                }
            }

            var lineInfo = textInfo.lineInfo[lineIdx];
            if (lineInfo.characterCount < 3)
            {
                return false;
            }

            var lastChar = characterInfos[lineInfo.lastCharacterIndex].character;
            if (lastChar == '\v')
            {
                lastChar = characterInfos[lineInfo.lastCharacterIndex - 1].character;
            }

            // If the line ends with hard line break, lastChar will be \n
            if (!IsChineseCharacter(lastChar))
            {
                return false;
            }

            return true;
        }

        // Visible character count is unchanged when typesetting
        private static int GetVisibleCharCountUntil(TMP_TextInfo textInfo, int lineIdx)
        {
            var count = 0;
            for (var i = 0; i <= lineIdx; ++i)
            {
                count += textInfo.lineInfo[i].visibleCharacterCount;
            }

            return count;
        }

        private static int GetIdxForVisibleChar(TMP_TextInfo textInfo, int count)
        {
            var characterInfos = textInfo.characterInfo;
            for (var i = 0; i < textInfo.lineCount; ++i)
            {
                var lineInfo = textInfo.lineInfo[i];
                if (count > lineInfo.visibleCharacterCount)
                {
                    count -= lineInfo.visibleCharacterCount;
                }
                else if (count == lineInfo.visibleCharacterCount)
                {
                    return characterInfos[lineInfo.lastVisibleCharacterIndex].index;
                }
                else
                {
                    var lastCharIdx = lineInfo.lastVisibleCharacterIndex;
                    for (var charIdx = lineInfo.firstVisibleCharacterIndex; charIdx < lastCharIdx; ++charIdx)
                    {
                        var charInfo = characterInfos[charIdx];
                        if (!charInfo.isVisible)
                        {
                            continue;
                        }

                        --count;
                        if (count == 0)
                        {
                            return charInfo.index;
                        }
                    }

                    throw new ArgumentOutOfRangeException();
                }
            }

            throw new ArgumentOutOfRangeException();
        }

        public static void ApplyKerning(TMP_Text textBox, RectTransform rectTransform,
            ref string text, ref TMP_TextInfo textInfo)
        {
            var idxs = new List<int>();
            var kerns = new List<float>();

            // Each line is updated only once. Even if some character is pulled to the previous line and forms a
            // punctuation pair, the previous line will not be updated again
            // That's also why we don't apply kerning at line ending
            for (var lineIdx = 0; lineIdx < textInfo.lineCount; ++lineIdx)
            {
                var oldText = text;
                var canAbsorbWidths = GetCanAbsorbWidths(textBox, textInfo, lineIdx + 1);
                ApplyKerningLine(textBox, rectTransform, ref text, ref textInfo, lineIdx, idxs, kerns, canAbsorbWidths,
                    out var canAvoidOrphan);
                if (!canAvoidOrphan)
                {
                    continue;
                }

                canAvoidOrphan = PeekAvoidOrphan(textInfo, lineIdx);
                if (!canAvoidOrphan)
                {
                    continue;
                }

                var count = GetVisibleCharCountUntil(textInfo, lineIdx);

                // Rollback
                // There may be bugs if we save oldTextInfo
                text = oldText;
                textInfo = textBox.GetTextInfo(text);

                var idx = GetIdxForVisibleChar(textInfo, count);
                text = text.Insert(idx, "\v");
                textInfo = textBox.GetTextInfo(text);
                ApplyKerningLine(textBox, rectTransform, ref text, ref textInfo, lineIdx, idxs, kerns, canAbsorbWidths,
                    out canAvoidOrphan);
            }
        }

        // private static void DebugPrint(TMP_TextInfo textInfo, int lineIdx)
        // {
        //     var characterInfos = textInfo.characterInfo;
        //     var lineInfo = textInfo.lineInfo[lineIdx];
        //     Debug.Log(
        //         $"{lineIdx} {characterInfos[lineInfo.firstVisibleCharacterIndex].character} {characterInfos[lineInfo.lastVisibleCharacterIndex].character}"
        //     );
        //     // for (var i = lineInfo.firstCharacterIndex; i <= lineInfo.lastCharacterIndex; ++i)
        //     // {
        //     //     var c = characterInfos[i];
        //     //     Debug.Log($"{c.character}");
        //     // }
        // }
    }
}
