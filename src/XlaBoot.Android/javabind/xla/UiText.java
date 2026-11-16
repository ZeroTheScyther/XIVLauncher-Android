package xla;

import android.content.Context;

import java.util.HashMap;
import java.util.Locale;
import java.util.Map;

/**
 * The in-game overlay's text (menu, keyboard bar, control editor) in the launcher's language.
 *
 * Follows the launcher's "launcher_language" setting, read from the same preferences, so the overlay always matches
 * the launcher; "system" resolves to the phone's language when it is one of ours. Read once per game session: the
 * setting can only change in the launcher.
 *
 * The launcher's own text lives in Strings.*.resx. Shared words (On, Off, Auto, Close, Done) use the same
 * translations as there. New overlay text needs all four columns.
 */
final class UiText {

    private static final String[] LANGUAGES = {"en", "fr", "de", "ja"};
    private static final Map<String, String[]> TEXT = new HashMap<>();
    private static int column;

    private static void add(String key, String en, String fr, String de, String ja) {
        TEXT.put(key, new String[]{en, fr, de, ja});
    }

    static {
        // Menu
        add("display", "Display", "Affichage", "Anzeige", "表示");
        add("controls", "Controls", "Commandes", "Steuerung", "操作");
        add("game", "Game", "Jeu", "Spiel", "ゲーム");
        add("performance_overlay", "Performance overlay", "Affichage des performances", "Leistungsanzeige", "パフォーマンス表示");
        add("upscaler", "Upscaler", "Mise à l'échelle", "Upscaler", "アップスケーラー");
        add("screen_fit", "Screen fit", "Ajustement de l'écran", "Bildanpassung", "画面の合わせ方");
        add("onscreen_pad", "On-screen pad", "Manette à l'écran", "Bildschirm-Gamepad", "画面上のパッド");
        add("keyboard_button", "Keyboard button", "Bouton clavier", "Tastatur-Taste", "キーボードボタン");
        add("keyboard_auto_open", "Auto-open keyboard", "Ouverture auto du clavier", "Tastatur automatisch öffnen", "キーボードを自動で開く");
        add("onscreen_sticks", "On-screen sticks", "Sticks à l'écran", "Bildschirm-Sticks", "画面上のスティック");
        add("touchscreen", "Touchscreen", "Écran tactile", "Touchscreen", "タッチスクリーン");
        add("dead_zone", "Stick dead zone", "Zone morte des sticks", "Stick-Totzone", "スティックのデッドゾーン");
        add("edit_controls", "Edit on-screen controls", "Modifier les commandes à l'écran", "Bildschirmsteuerung bearbeiten", "画面上の操作を編集");
        add("back_to_game", "Back to game", "Retour au jeu", "Zurück zum Spiel", "ゲームに戻る");
        add("exit_game", "Exit game", "Quitter le jeu", "Spiel beenden", "ゲームを終了");
        add("exit_confirm", "Tap again to exit", "Touchez encore pour quitter", "Zum Beenden erneut tippen", "もう一度タップで終了");

        // Setting values
        add("on", "On", "Activé", "An", "オン");
        add("off", "Off", "Désactivé", "Aus", "オフ");
        add("auto", "Auto", "Auto", "Auto", "自動");
        add("fit", "Fit", "Ajuster", "Einpassen", "合わせる");
        add("stretch", "Stretch", "Étirer", "Strecken", "引き伸ばし");
        add("under_thumb", "Under thumb", "Sous le pouce", "Unter dem Daumen", "親指の位置");
        add("fixed", "Fixed", "Fixe", "Fest", "固定");
        add("tap_to_click", "Tap-to-click", "Toucher pour cliquer", "Tippen zum Klicken", "タップでクリック");
        add("touchpad", "Touchpad", "Pavé tactile", "Touchpad", "タッチパッド");

        // Keyboard bar
        add("message", "Message", "Message", "Nachricht", "メッセージ");
        add("send", "Send", "Envoyer", "Senden", "送信");
        add("close", "Close", "Fermer", "Schließen", "閉じる");
        add("nothing_to_send", "Nothing here the game can show.", "Rien ici que le jeu puisse afficher.",
                "Nichts davon kann das Spiel anzeigen.", "ゲームで表示できる文字がありません。");
        add("field_full", "That is as long as this field goes.", "Ce champ ne peut pas contenir plus.",
                "Mehr passt nicht in dieses Feld.", "この欄にはこれ以上入力できません。");
        add("symbols_removed", "Emoji and symbols the game cannot show were removed.",
                "Les émojis et symboles que le jeu ne peut pas afficher ont été retirés.",
                "Emojis und Zeichen, die das Spiel nicht anzeigen kann, wurden entfernt.",
                "ゲームで表示できない絵文字や記号を削除しました。");

        // Control editor
        add("reset", "Reset", "Réinitialiser", "Zurücksetzen", "リセット");
        add("tap_again", "Tap again", "Touchez encore", "Erneut tippen", "もう一度タップ");
        add("done", "Done", "Terminé", "Fertig", "完了");
    }

    private UiText() {}

    /** Picks the language. Called when the game screen is built, before any overlay text is shown. */
    static void init(Context context) {
        String setting = "system";
        try {
            setting = context.getSharedPreferences("xla_settings", Context.MODE_PRIVATE)
                    .getString("launcher_language", "system");
        } catch (ClassCastException ignored) {
            // Not a string: treat as unset.
        }
        if (setting == null || "system".equals(setting))
            setting = Locale.getDefault().getLanguage();
        column = 0;
        for (int i = 0; i < LANGUAGES.length; i++)
            if (LANGUAGES[i].equals(setting)) column = i;
    }

    static String get(String key) {
        String[] row = TEXT.get(key);
        return row == null ? key : row[column];
    }

    static String onOff(boolean on) {
        return get(on ? "on" : "off");
    }

    /** "Label: value", the menu's row format. */
    static String row(String key, String value) {
        return get(key) + (column == 1 ? " : " : ": ") + value;
    }
}
