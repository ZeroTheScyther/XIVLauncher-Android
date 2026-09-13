package xla;

/**
 * Turns what an Android keyboard produces into something FFXIV will take.
 *
 * A phone keyboard offers emoji, smart quotes, en dashes and non-breaking spaces. The game has none of them: its
 * font carries ASCII, the Latin-1 letters European names need, and CJK where the field allows it, and it silently
 * refuses a string containing anything else. Refusing it here instead means the player sees what the game will get
 * while they are still typing, rather than pressing send on a line that never appears.
 *
 * Plain Java on purpose: no android imports, so the rules can be tested on a PC (tests/TextFilterTest).
 */
final class GameText {

    /** The game's own AllowedEntities bit for CJK, as reported by the helper plugin for the focused field. */
    static final int ALLOW_CJK = 1024;

    private GameText() {}

    /** Filters and then clamps, which is everything a field needs applied in the right order. */
    static String forField(String input, int allow, boolean multiline, int maxChar, int maxByte) {
        return clamp(filter(input, allow, multiline), maxChar, maxByte);
    }

    /**
     * Drops every character the game cannot render and rewrites the ones a phone substitutes for ASCII.
     * CJK is kept only when the field says it takes it, or when the field's limits are unknown.
     */
    static String filter(String input, int allow, boolean multiline) {
        if (input == null || input.isEmpty()) return "";
        boolean cjk = allow == 0 || (allow & ALLOW_CJK) != 0;

        StringBuilder out = new StringBuilder(input.length());
        int i = 0;
        while (i < input.length()) {
            int cp = input.codePointAt(i);
            i += Character.charCount(cp);

            String replacement = substitute(cp);
            if (replacement != null) {
                out.append(replacement);
                continue;
            }
            if (cp == '\n') {
                if (multiline) out.append('\n');
                else out.append(' ');   // a pasted line break in a single-line field becomes a space, not a loss
                continue;
            }
            if (allowed(cp, cjk)) out.appendCodePoint(cp);
        }
        return out.toString();
    }

    /**
     * Cuts a string to the field's own limits: characters first, then UTF-8 bytes. A character is never split in
     * half, so the result is always something the game can read.
     */
    static String clamp(String text, int maxChar, int maxByte) {
        if (text == null || text.isEmpty()) return "";
        if (maxChar > 0 && text.codePointCount(0, text.length()) > maxChar)
            text = text.substring(0, text.offsetByCodePoints(0, maxChar));
        if (maxByte <= 0 || utf8Length(text) <= maxByte) return text;

        int bytes = 0;
        int end = 0;
        int i = 0;
        while (i < text.length()) {
            int cp = text.codePointAt(i);
            int width = Character.charCount(cp);
            int size = utf8Length(new String(Character.toChars(cp)));
            if (bytes + size > maxByte) break;
            bytes += size;
            i += width;
            end = i;
        }
        return text.substring(0, end);
    }

    /** Whether anything was removed, so the keyboard can say why quietly. */
    static boolean changed(String input, String result) {
        return input != null && !input.equals(result);
    }

    /** What a phone types where the game expects ASCII. Null when the character is not one of them. */
    private static String substitute(int cp) {
        switch (cp) {
            case 0x00A0:            // non-breaking space
            case 0x2007:
            case 0x202F:
            case 0x3000: return " ";
            case 0x2018:
            case 0x2019:
            case 0x201B: return "'";
            case 0x201C:
            case 0x201D: return "\"";
            case 0x2010:
            case 0x2011:
            case 0x2012:
            case 0x2013:
            case 0x2014:
            case 0x2015: return "-";
            case 0x2026: return "...";
            case 0x00AD: return "";   // soft hyphen: invisible, and the game keeps it as a box
            default: return null;
        }
    }

    private static boolean allowed(int cp, boolean cjk) {
        if (cp >= 0x20 && cp <= 0x7E) return true;                  // printable ASCII
        if (cp >= 0xA1 && cp <= 0xFF) return true;                  // Latin-1: accented letters, punctuation
        if (cp >= 0x100 && cp <= 0x17F) return true;                // Latin Extended-A, used by some names
        if (!cjk) return false;
        return (cp >= 0x3001 && cp <= 0x30FF)                       // CJK punctuation and kana
                || (cp >= 0x4E00 && cp <= 0x9FFF)                   // unified ideographs
                || (cp >= 0xF900 && cp <= 0xFAFF)                   // compatibility ideographs
                || (cp >= 0xFF01 && cp <= 0xFF60)                   // fullwidth forms
                || (cp >= 0xFFE0 && cp <= 0xFFE6);
    }

    private static int utf8Length(String text) {
        int bytes = 0;
        for (int i = 0; i < text.length(); ) {
            int cp = text.codePointAt(i);
            i += Character.charCount(cp);
            bytes += cp < 0x80 ? 1 : cp < 0x800 ? 2 : cp < 0x10000 ? 3 : 4;
        }
        return bytes;
    }
}
