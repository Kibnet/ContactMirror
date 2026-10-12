using ContactMirror.Core;

namespace ContactMirror.Application.Contacts;

/// <summary>Russian presentation only. Tokens and schema validation remain unchanged.</summary>
public static class ContactPresentationCatalog
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<ContactFieldOption>> Choices = BuildChoices();
    private static readonly HashSet<string> Closed = ["biographies.contentType", "nicknames.type", "miscKeywords.type"];
    private static readonly IReadOnlyList<ContactFieldOption> NoChoices = Array.AsReadOnly(Array.Empty<ContactFieldOption>());

    public static string Title(string category, string? property = null)
    {
        if (string.IsNullOrEmpty(property)) return CapabilityRegistry.Title(category);
        if (property.StartsWith("data." + category + ".", StringComparison.Ordinal)) property = property[(category.Length + 6)..];
        if (property == "metadata.sourcePrimary" || property == "sourcePrimary") return category switch
        {
            "phoneNumbers" => "Основной телефон", "emailAddresses" => "Основная почта", "addresses" => "Основной адрес",
            "organizations" => "Основная организация", _ => "Основная запись"
        };
        if (property.Contains('.'))
        {
            var parts = property.Split('.');
            if (parts[^1] is "year" or "month" or "day")
            {
                var component = ContactFieldSchema.FieldTitle(parts[^1]);
                var context = category == "birthdays" ? "рождения" : parts[^2] == "startDate" ? "начала" : parts[^2] == "endDate" ? "окончания" : "события";
                return component + " " + context;
            }
            return Title(category, parts[^1]);
        }
        if (property == "value") return category switch
        {
            "phoneNumbers" => "Номер телефона", "emailAddresses" => "Адрес электронной почты", "biographies" => "Текст заметки",
            "genders" => "Пол", "sipAddresses" => "SIP-адрес", "urls" => "Адрес сайта", "interests" => "Интерес",
            "locales" => "Язык (тег)", "locations" => "Местоположение", "miscKeywords" => "Ключевое слово",
            "nicknames" => "Псевдоним", "occupations" => "Занятие", "externalIds" => "Внешний идентификатор",
            "clientData" => "Значение параметра", "userDefined" => "Содержимое поля", _ => "Содержимое записи"
        };
        if (property == "type") return category switch
        {
            "phoneNumbers" => "Тип телефона", "emailAddresses" => "Тип почты", "addresses" => "Тип адреса",
            "organizations" => "Тип организации", "relations" => "Отношение", "events" => "Тип события",
            "calendarUrls" => "Тип календаря", "urls" => "Тип ссылки", "imClients" => "Тип аккаунта мессенджера",
            "sipAddresses" => "Тип SIP-адреса", "locations" => "Тип местоположения", "nicknames" => "Тип псевдонима",
            "externalIds" => "Тип идентификатора", "miscKeywords" => "Тип ключевого слова", _ => "Тип записи"
        };
        return (category, property) switch
        {
            ("organizations", "name") => "Название организации", ("organizations", "current") => "Работаю сейчас",
            ("organizations", "fullTimeEquivalentMillipercent") => "Занятость, %", ("organizations", "startDate") => "Дата начала",
            ("organizations", "endDate") => "Дата окончания", ("organizations", "location") => "Место работы",
            ("calendarUrls", "url") => "Адрес календаря", ("relations", "person") => "Имя человека",
            ("clientData", "key") => "Название параметра", ("userDefined", "key") => "Название поля",
            ("birthdays", "date") => "Дата рождения", ("events", "date") => "Дата события",
            ("birthdays", "text") => "Сохранённый текст даты", ("imClients", "username") => "Аккаунт мессенджера",
            ("imClients", "protocol") => "Мессенджер", ("genders", "addressMeAs") => "Как обращаться",
            (_, "metadata") => "Предпочтение для этой записи", _ => ContactFieldSchema.FieldTitle(property)
        };
    }

    public static IReadOnlyList<ContactFieldOption> Options(string category, string property) => Choices.GetValueOrDefault(category + "." + property, NoChoices);
    public static bool AllowsCustom(string category, string property) => Choices.ContainsKey(category + "." + property) && !Closed.Contains(category + "." + property);
    public static string OptionTitle(string category, string property, string raw) => Options(category, property).FirstOrDefault(option => option.Value == raw)?.Title ?? raw;

    private static IReadOnlyDictionary<string, IReadOnlyList<ContactFieldOption>> BuildChoices()
    {
        var result = new Dictionary<string, IReadOnlyList<ContactFieldOption>>(StringComparer.Ordinal);
        Add("phoneNumbers", "type", ("home", "Домашний"), ("work", "Рабочий"), ("mobile", "Мобильный"), ("homeFax", "Домашний факс"),
            ("workFax", "Рабочий факс"), ("otherFax", "Другой факс"), ("pager", "Пейджер"), ("workMobile", "Рабочий мобильный"),
            ("workPager", "Рабочий пейджер"), ("main", "Главный"), ("googleVoice", "Google Voice"), ("other", "Другой"));
        Add("emailAddresses", "type", ("home", "Личный"), ("work", "Рабочий"), ("other", "Другой"));
        foreach (var category in new[] { "addresses", "imClients" }) Add(category, "type", ("home", "Домашний"), ("work", "Рабочий"), ("other", "Другой"));
        Add("sipAddresses", "type", ("home", "Домашний"), ("work", "Рабочий"), ("mobile", "Мобильный"), ("other", "Другой"));
        Add("calendarUrls", "type", ("home", "Личный"), ("freeBusy", "Свободен / занят"), ("work", "Рабочий"));
        Add("urls", "type", ("home", "Личный"), ("work", "Рабочий"), ("blog", "Блог"), ("profile", "Профиль"), ("homePage", "Домашняя страница"),
            ("ftp", "FTP"), ("reservations", "Бронирование"), ("appInstallPage", "Страница приложения"), ("other", "Другая ссылка"));
        Add("organizations", "type", ("work", "Работа"), ("school", "Учёба"));
        Add("relations", "type", ("spouse", "Супруг / супруга"), ("child", "Ребёнок"), ("mother", "Мать"), ("father", "Отец"), ("parent", "Родитель"),
            ("brother", "Брат"), ("sister", "Сестра"), ("friend", "Друг / подруга"), ("relative", "Родственник"), ("domesticPartner", "Гражданский партнёр"),
            ("manager", "Руководитель"), ("assistant", "Помощник"), ("referredBy", "Рекомендовал"), ("partner", "Партнёр"));
        Add("imClients", "protocol", ("aim", "AIM"), ("msn", "MSN"), ("yahoo", "Yahoo"), ("skype", "Skype"), ("qq", "QQ"),
            ("googleTalk", "Google Talk"), ("icq", "ICQ"), ("jabber", "Jabber"), ("netMeeting", "NetMeeting"));
        Add("locations", "type", ("desk", "Рабочее место"), ("grewUp", "Место детства"));
        Add("externalIds", "type", ("account", "Аккаунт"), ("customer", "Клиент"), ("loginId", "Логин"), ("network", "Сеть"), ("organization", "Организация"));
        Add("events", "type", ("anniversary", "Годовщина"), ("other", "Другое событие"));
        Add("genders", "value", ("male", "Мужской"), ("female", "Женский"), ("unspecified", "Не указан"));
        Add("biographies", "contentType", ("CONTENT_TYPE_UNSPECIFIED", "Не задан"), ("TEXT_PLAIN", "Обычный текст"), ("TEXT_HTML", "HTML (исходный текст)"));
        Add("nicknames", "type", ("DEFAULT", "Обычный"), ("MAIDEN_NAME", "Девичья фамилия"), ("INITIALS", "Инициалы"), ("GPLUS", "Имя Google+"),
            ("OTHER_NAME", "Другое имя"), ("ALTERNATE_NAME", "Альтернативное имя"), ("SHORT_NAME", "Короткое имя"));
        Add("miscKeywords", "type", ("TYPE_UNSPECIFIED", "Не задан"), ("OUTLOOK_BILLING_INFORMATION", "Сведения об оплате Outlook"),
            ("OUTLOOK_DIRECTORY_SERVER", "Сервер каталога Outlook"), ("OUTLOOK_KEYWORD", "Ключевое слово Outlook"), ("OUTLOOK_MILEAGE", "Пробег Outlook"),
            ("OUTLOOK_PRIORITY", "Приоритет Outlook"), ("OUTLOOK_SENSITIVITY", "Конфиденциальность Outlook"), ("OUTLOOK_SUBJECT", "Тема Outlook"),
            ("OUTLOOK_USER", "Пользователь Outlook"), ("HOME", "Домашний"), ("WORK", "Рабочий"), ("OTHER", "Другой"));
        return new System.Collections.ObjectModel.ReadOnlyDictionary<string, IReadOnlyList<ContactFieldOption>>(result);
        void Add(string category, string property, params (string Value, string Title)[] values) =>
            result.Add(category + "." + property, Array.AsReadOnly(values.Select(value => new ContactFieldOption(value.Value, value.Title)).ToArray()));
    }
}
