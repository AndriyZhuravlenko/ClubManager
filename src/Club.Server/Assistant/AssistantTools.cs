using System.Text.Json.Nodes;

namespace Club.Server.Assistant;

/// <summary>
/// Опис функцій, які модель може «викликати». Модель лише обирає функцію й аргументи —
/// виконує їх сервер (читання даних) або панель (дії після підтвердження адміністратором).
/// Нічого поза цим списком асистент зробити не може.
/// </summary>
public static class AssistantTools
{
    // Дії, що змінюють стан системи: виконуються лише після кнопки «Так» у чаті
    public static readonly HashSet<string> NeedConfirmation =
        ["pc_command", "start_guest_session", "extend_session", "end_session", "top_up_client", "set_client_blocked", "add_blocked_app",
         "close_app", "launch_app"];

    public static JsonArray Declarations(bool isOwner)
    {
        string[] periodEnum = ["today", "7d", "30d"];
        string[] tabs = isOwner ? new[] { "pcs", "clients", "apps", "events", "reports", "admins" } : new[] { "pcs", "clients", "apps", "events" };
        var tools = new JsonArray
        {
            // ---------- керування панеллю ----------
            Fn("show_tab", "Відкрити вкладку панелі адміністратора.",
                Props(("tab", Enum("Вкладка: pcs — комп'ютери (карта клубу), clients — клієнти, apps — дозволені й заборонені програми, " +
                                   "events — журнал подій" + (isOwner ? ", reports — звіти, admins — адміністратори" : ""),
                                   tabs))),
                "tab"),
            Fn("filter_journal",
                "Відкрити журнал подій у панелі з фільтрами. Параметри, які не згадано, скидаються до «усі».",
                Props(
                    ("pc", Str("Назва ПК; 'panel' — події панелі без ПК; порожньо — усі ПК")),
                    ("category", Enum("Хто виконав дію", ["all", "admin", "client", "agent", "system"])),
                    ("text", Str("Текст для пошуку в подіях або логін")),
                    ("period", Enum("Період", ["all", "today", "24h", "7d", "30d"])),
                    ("limit", Enum("Скільки записів показати", ["100", "300", "1000"])))),
            Fn("open_pc", "Відкрити на панелі вікно дій з конкретним ПК.",
                Props(("pc", Str("Назва ПК"))), "pc"),

            // ---------- читання даних ----------
            Fn("get_pcs_status", "Поточний стан усіх ПК: у мережі чи ні, вільний/зайнятий/призупинений, хто сидить, скільки хвилин залишилось.", null),
            Fn("get_clients", "Список клієнтів: логін, баланс у хвилинах, чи заблокований, на якому ПК зараз працює.",
                Props(("search", Str("Частина логіна для пошуку (необов'язково)")))),
            Fn("get_events", "Прочитати записи журналу подій (щоб відповісти на запитання, що відбувалося). Не змінює панель.",
                Props(
                    ("pc", Str("Назва ПК; 'panel' — події панелі; порожньо — усі")),
                    ("category", Enum("Хто виконав дію", ["all", "admin", "client", "agent", "system"])),
                    ("text", Str("Текст для пошуку")),
                    ("period", Enum("Період", ["all", "today", "24h", "7d", "30d"])),
                    ("limit", Int("Скільки останніх записів, до 30")))),
            Fn("get_apps", "Дозволені програми (каталог), чорний список і режим контролю програм.", null),
            Fn("get_running_apps", "Які програми зараз запущені на конкретному ПК (у сеансі користувача).",
                Props(("pc", Str("Назва ПК"))), "pc"),

            // ---------- дії (лише з підтвердженням) ----------
            Fn("pc_command",
                "Запропонувати команду для одного чи кількох ПК. Виконається лише після підтвердження адміністратором. " +
                "lock на ПК із сеансом призупиняє сеанс, unlock — відновлює.",
                Props(
                    ("pcs", Arr("Назви ПК; 'all_online' — усі ПК у мережі")),
                    ("command", Enum("Команда", ["message", "lock", "unlock", "reboot", "shutdown"])),
                    ("text", Str("Текст повідомлення (лише для message)"))),
                "pcs", "command"),
            Fn("start_guest_session", "Запропонувати гостьовий сеанс на вільних ПК. Виконається після підтвердження.",
                Props(("pcs", Arr("Назви ПК; 'all_free' — усі вільні ПК у мережі")), ("minutes", Int("Тривалість, хв (1–1440)"))),
                "pcs", "minutes"),
            Fn("extend_session", "Запропонувати подовжити гостьовий сеанс на ПК. Виконається після підтвердження.",
                Props(("pc", Str("Назва ПК")), ("minutes", Int("На скільки хвилин"))), "pc", "minutes"),
            Fn("end_session", "Запропонувати завершити сеанси на ПК. Виконається після підтвердження.",
                Props(("pcs", Arr("Назви ПК; 'all_busy' — усі ПК із сеансом"))), "pcs"),
            Fn("top_up_client", "Запропонувати поповнити баланс клієнта (у хвилинах). Виконається після підтвердження.",
                Props(("login", Str("Логін клієнта")), ("minutes", Int("Скільки хвилин додати (1–10000)"))), "login", "minutes"),
            Fn("set_client_blocked", "Запропонувати заблокувати або розблокувати обліковий запис клієнта. Виконається після підтвердження.",
                Props(("login", Str("Логін клієнта")), ("blocked", Bool("true — заблокувати, false — розблокувати"))), "login", "blocked"),
            Fn("close_app", "Запропонувати закрити запущену програму на ПК (усі її вікна й процеси). Виконається після підтвердження.",
                Props(("pc", Str("Назва ПК")), ("app", Str("Назва програми або ім'я файлу, напр. Steam чи steam.exe"))), "pc", "app"),
            Fn("launch_app", "Запропонувати запустити програму з каталогу дозволених на ПК з активним сеансом. Виконається після підтвердження.",
                Props(("pc", Str("Назва ПК")), ("app", Str("Назва програми з каталогу"))), "pc", "app"),
            Fn("add_blocked_app", "Запропонувати додати програму в чорний список (ім'я файлу, шаблон з * або шлях). Виконається після підтвердження.",
                Props(("pattern", Str("Напр. taskmgr.exe, cheat*.exe або C:\\Games\\Old"))), "pattern"),
        };
        if (isOwner)
            tools.Add(Fn("get_session_stats", "Статистика за період: кількість сеансів за видами, години роботи ПК, списані та поповнені хвилини.",
                Props(("period", Enum("Період", periodEnum))), "period"));
        return tools;
    }

    // ---------- допоміжне для опису схем (формат OpenAPI, який приймає Gemini) ----------

    private static JsonObject Fn(string name, string description, JsonObject? parameters, params string[] required)
    {
        var fn = new JsonObject { ["name"] = name, ["description"] = description };
        if (parameters != null)
        {
            var schema = new JsonObject { ["type"] = "OBJECT", ["properties"] = parameters };
            if (required.Length > 0) schema["required"] = new JsonArray(required.Select(r => (JsonNode)r).ToArray());
            fn["parameters"] = schema;
        }
        return fn;
    }

    private static JsonObject Props(params (string Name, JsonObject Schema)[] props)
    {
        var o = new JsonObject();
        foreach (var (name, schema) in props) o[name] = schema;
        return o;
    }

    private static JsonObject Str(string d) => new() { ["type"] = "STRING", ["description"] = d };
    private static JsonObject Int(string d) => new() { ["type"] = "INTEGER", ["description"] = d };
    private static JsonObject Bool(string d) => new() { ["type"] = "BOOLEAN", ["description"] = d };
    private static JsonObject Arr(string d) => new() { ["type"] = "ARRAY", ["description"] = d, ["items"] = new JsonObject { ["type"] = "STRING" } };
    private static JsonObject Enum(string d, string[] values) =>
        new() { ["type"] = "STRING", ["description"] = d, ["enum"] = new JsonArray(values.Select(v => (JsonNode)v).ToArray()) };
}
