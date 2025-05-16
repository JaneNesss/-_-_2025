using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using File = System.IO.File;

namespace SkinCareTrackerBot
{
    public enum TimeCategory
    {
        Morning,
        BeforeMakeup,
        Evening,
        Anytime
    }

    class Program
    {
        private static ITelegramBotClient _botClient = null!;
        private static readonly Dictionary<long, UserState> _userStates = new();
        private static readonly Dictionary<long, Dictionary<string, DateTime>> _userReminders = new();
        private const string DataFilePath = "userdata.txt";
        private static Timer _reminderCheckTimer = null!;

       

        static async Task Main()
        {
            try
            {
                StartBot();
                Console.WriteLine("Нажмите любую клавишу для выхода...");
                Console.ReadKey();
            }
            finally
            {
                _reminderCheckTimer?.Dispose();
            }
        }

        private static void StartBot()
        {
            _botClient = new TelegramBotClient("7756749897:AAEp70qM8qEMgSTCPc8ct_IywAMZsy1GOf4");
            LoadUserData();

            _reminderCheckTimer = new Timer(CheckReminders, null, TimeSpan.Zero, TimeSpan.FromMinutes(1));

            using var cts = new CancellationTokenSource();

            var receiverOptions = new ReceiverOptions
            {
                AllowedUpdates = Array.Empty<UpdateType>()
            };

            _botClient.StartReceiving(
                updateHandler: HandleUpdateAsync,
                pollingErrorHandler: HandleErrorAsync,
                receiverOptions: receiverOptions,
                cancellationToken: cts.Token
            );

            Console.WriteLine("Бот запущен!");
        }

        private static void LoadUserData()
        {
            if (!File.Exists(DataFilePath)) return;

            foreach (var line in File.ReadAllLines(DataFilePath))
            {
                var parts = line.Split('|');
                if (parts.Length < 2) continue;

                if (!long.TryParse(parts[0], out var userId)) continue;

                var productsData = parts[1].Split(';', StringSplitOptions.RemoveEmptyEntries);
                var products = productsData.Select(p =>
                {
                    var productParts = p.Split('~');
                    return new CosmeticProduct
                    {
                        Name = productParts[0],
                        TimeCategory = productParts.Length > 1 ? (TimeCategory)Enum.Parse(typeof(TimeCategory), productParts[1]) : TimeCategory.Anytime,
                        MainComponent = productParts.Length > 2 ? productParts[2] : string.Empty,
                        Purpose = productParts.Length > 3 ? productParts[3] : string.Empty
                    };
                }).ToList();

                _userStates[userId] = new UserState { Products = products };

                if (parts.Length <= 2) continue;

                foreach (var reminder in parts[2].Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    var reminderParts = reminder.Split(':');
                    if (reminderParts.Length != 2) continue;

                    var productName = reminderParts[0];
                    if (DateTime.TryParseExact(reminderParts[1], "HH:mm", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var time))
                    {
                        if (!_userReminders.ContainsKey(userId))
                        {
                            _userReminders[userId] = new();
                        }
                        _userReminders[userId][productName] = time;
                    }
                }
            }
        }

        private static void SaveUserData()
        {
            var lines = new List<string>();

            foreach (var (userId, state) in _userStates)
            {
                var productsData = state.Products.Select(p =>
                    $"{p.Name}~{p.TimeCategory}~{p.MainComponent}~{p.Purpose}");
                var products = string.Join(";", productsData);

                var line = $"{userId}|{products}";

                if (_userReminders.TryGetValue(userId, out var reminders) && reminders.Count > 0)
                {
                    line += "|" + string.Join(";", reminders.Select(r => $"{r.Key}:{r.Value:HH:mm}"));
                }

                lines.Add(line);
            }

            File.WriteAllLines(DataFilePath, lines);
        }

        private static async void CheckReminders(object? state)
        {
            var now = DateTime.Now;

            foreach (var (userId, reminders) in _userReminders)
            {
                foreach (var (product, time) in reminders)
                {
                    if (now.Hour == time.Hour && now.Minute == time.Minute)
                    {
                        try
                        {
                            await _botClient.SendTextMessageAsync(
                                chatId: userId,
                                text: $"⏰ Напоминание: время для {product}!"
                            );
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Ошибка отправки напоминания: {ex.Message}");
                        }
                    }
                }
            }
        }

        private static async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
        {
            if (update.Message is not { Text: { } messageText } || update.Message.Chat is not { Id: var chatId })
                return;

            Console.WriteLine($"Получено сообщение: '{messageText}' в чате {chatId}");

            if (!_userStates.TryGetValue(chatId, out var userState))
            {
                userState = new UserState();
                _userStates[chatId] = userState;
            }

            try
            {
                switch (messageText)
                {
                    case "/start":
                    case "Начать с начала":
                        await SendStartMessage(chatId, cancellationToken);
                        break;
                    case "Мои средства":
                        await ShowProductsList(chatId, cancellationToken);
                        break;
                    case "Добавить средство":
                        userState.CurrentAction = UserAction.AddingProduct;
                        await botClient.SendTextMessageAsync(
                            chatId: chatId,
                            text: "Введите название косметического средства:",
                            replyMarkup: new ReplyKeyboardRemove(),
                            cancellationToken: cancellationToken);
                        break;
                    case "Удалить средство":
                        if (userState.Products.Count == 0)
                        {
                            await botClient.SendTextMessageAsync(
                                chatId: chatId,
                                text: "Ваш список средств пуст.",
                                replyMarkup: GetMainMenuKeyboard(),
                                cancellationToken: cancellationToken);
                            break;
                        }
                        userState.CurrentAction = UserAction.RemovingProduct;
                        await ShowProductsListWithNumbers(chatId, cancellationToken);
                        break;
                    case "Добавить напоминание":
                        if (userState.Products.Count == 0)
                        {
                            await botClient.SendTextMessageAsync(
                                chatId: chatId,
                                text: "Сначала добавьте косметические средства.",
                                replyMarkup: GetMainMenuKeyboard(),
                                cancellationToken: cancellationToken);
                            break;
                        }
                        userState.CurrentAction = UserAction.AddingReminder;
                        await ShowProductsListWithNumbers(chatId, cancellationToken);
                        break;
                    case "Мои напоминания":
                        await ShowRemindersList(chatId, cancellationToken);
                        break;
                    case "Удалить напоминание":
                        if (!_userReminders.TryGetValue(chatId, out var reminders) || reminders.Count == 0)
                        {
                            await botClient.SendTextMessageAsync(
                                chatId: chatId,
                                text: "У вас нет активных напоминаний.",
                                replyMarkup: GetMainMenuKeyboard(),
                                cancellationToken: cancellationToken);
                            break;
                        }
                        userState.CurrentAction = UserAction.RemovingReminder;
                        await ShowRemindersListWithNumbers(chatId, cancellationToken);
                        break;
                    case "Перезапустить бота":
                        await botClient.SendTextMessageAsync(
                            chatId: chatId,
                            text: "Бот перезапускается...",
                            replyMarkup: new ReplyKeyboardRemove(),
                            cancellationToken: cancellationToken);
                        StartBot();
                        await SendStartMessage(chatId, cancellationToken);
                        break;
                    default:
                        await HandleUserInput(chatId, messageText, userState, cancellationToken);
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка обработки сообщения: {ex.Message}");
                await botClient.SendTextMessageAsync(
                    chatId: chatId,
                    text: "Произошла ошибка. Пожалуйста, попробуйте еще раз.",
                    replyMarkup: GetMainMenuKeyboard(),
                    cancellationToken: cancellationToken);
            }
        }

        private static async Task HandleUserInput(long chatId, string messageText, UserState userState, CancellationToken cancellationToken)
        {
            switch (userState.CurrentAction)
            {
                case UserAction.AddingProduct:
                    var existingProduct = userState.Products.FirstOrDefault(p => p.Name.Equals(messageText, StringComparison.OrdinalIgnoreCase));
                    if (existingProduct != null)
                    {
                        await _botClient.SendTextMessageAsync(
                            chatId: chatId,
                            text: $"Средство \"{existingProduct.Name}\" уже есть в вашем списке.\nКатегория: {GetTimeCategoryName(existingProduct.TimeCategory)}\nКомпонент: {existingProduct.MainComponent}\nНазначение: {existingProduct.Purpose}",
                            replyMarkup: GetMainMenuKeyboard(),
                            cancellationToken: cancellationToken);
                    }
                    else
                    {
                        userState.TempProduct = new CosmeticProduct { Name = messageText };
                        userState.CurrentAction = UserAction.SelectingTimeCategory;
                        await _botClient.SendTextMessageAsync(
                            chatId: chatId,
                            text: "В какое время вы будете использовать данное средство?",
                            replyMarkup: GetTimeCategoryKeyboard(),
                            cancellationToken: cancellationToken);
                    }
                    break;

                case UserAction.SelectingTimeCategory:
                    if (Enum.TryParse<TimeCategory>(messageText, out var timeCategory))
                    {
                        userState.TempProduct.TimeCategory = timeCategory;
                        userState.CurrentAction = UserAction.AddingComponent;
                        await _botClient.SendTextMessageAsync(
                            chatId: chatId,
                            text: "Введите основной компонент продукта и его назначение в формате:\n\"Компонент-назначение\"\n\nПример: \"Panthenol-увлажнение, питание\"\n\nОсновной компонент вы можете найти в первой строке состава продукта. Состав пишут на упаковке, на самом продукте или на карточке товара в магазине, где был куплен товар.",
                            replyMarkup: new ReplyKeyboardRemove(),
                            cancellationToken: cancellationToken);
                    }
                    else
                    {
                        await _botClient.SendTextMessageAsync(
                            chatId: chatId,
                            text: "Пожалуйста, выберите категорию времени из предложенных вариантов.",
                            replyMarkup: GetTimeCategoryKeyboard(),
                            cancellationToken: cancellationToken);
                    }
                    break;

                case UserAction.AddingComponent:
                    var componentParts = messageText.Split('-', 2);
                    if (componentParts.Length == 2)
                    {
                        userState.TempProduct.MainComponent = componentParts[0].Trim();
                        userState.TempProduct.Purpose = componentParts[1].Trim();
                        userState.Products.Add(userState.TempProduct);
                        SaveUserData();

                        await _botClient.SendTextMessageAsync(
                            chatId: chatId,
                            text: $"Средство \"{userState.TempProduct.Name}\" добавлено!\nКатегория: {GetTimeCategoryName(userState.TempProduct.TimeCategory)}\nКомпонент: {userState.TempProduct.MainComponent}\nНазначение: {userState.TempProduct.Purpose}",
                            replyMarkup: GetMainMenuKeyboard(),
                            cancellationToken: cancellationToken);
                    }
                    else
                    {
                        await _botClient.SendTextMessageAsync(
                            chatId: chatId,
                            text: "Неверный формат. Пожалуйста, введите компонент и назначение в формате:\n\"Компонент-назначение\"\n\nПример: \"Panthenol-увлажнение, питание\"",
                            replyMarkup: new ReplyKeyboardRemove(),
                            cancellationToken: cancellationToken);
                        return;
                    }
                    userState.CurrentAction = UserAction.None;
                    userState.TempProduct = null;
                    break;

                case UserAction.RemovingProduct:
                    if (int.TryParse(messageText, out var productIndex) && productIndex > 0 && productIndex <= userState.Products.Count)
                    {
                        var product = userState.Products[productIndex - 1];
                        userState.Products.RemoveAt(productIndex - 1);

                        if (_userReminders.TryGetValue(chatId, out var userReminders))
                        {
                            userReminders.Remove(product.Name);
                        }

                        SaveUserData();
                        await _botClient.SendTextMessageAsync(
                            chatId: chatId,
                            text: $"Средство \"{product.Name}\" удалено!",
                            replyMarkup: GetMainMenuKeyboard(),
                            cancellationToken: cancellationToken);
                    }
                    else
                    {
                        await _botClient.SendTextMessageAsync(
                            chatId: chatId,
                            text: "Неверный номер средства. Пожалуйста, попробуйте еще раз.",
                            replyMarkup: GetProductsListKeyboard(userState.Products),
                            cancellationToken: cancellationToken);
                        return;
                    }
                    userState.CurrentAction = UserAction.None;
                    break;

                case UserAction.AddingReminder:
                    if (int.TryParse(messageText, out var reminderProductIndex) &&
                        reminderProductIndex > 0 && reminderProductIndex <= userState.Products.Count)
                    {
                        var product = userState.Products[reminderProductIndex - 1];
                        userState.TempProductForReminder = product.Name;
                        userState.CurrentAction = UserAction.SettingReminderTime;
                        await _botClient.SendTextMessageAsync(
                            chatId: chatId,
                            text: $"Введите время напоминания для \"{product.Name}\" (формат ЧЧ:ММ, например 09:00 или 21:30):",
                            replyMarkup: new ReplyKeyboardRemove(),
                            cancellationToken: cancellationToken);
                    }
                    else
                    {
                        await _botClient.SendTextMessageAsync(
                            chatId: chatId,
                            text: "Неверный номер средства. Пожалуйста, попробуйте еще раз.",
                            replyMarkup: GetProductsListKeyboard(userState.Products),
                            cancellationToken: cancellationToken);
                    }
                    break;

                case UserAction.SettingReminderTime:
                    if (DateTime.TryParseExact(messageText, "HH:mm", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var reminderTime))
                    {
                        var product = userState.TempProductForReminder ?? string.Empty;

                        if (!_userReminders.TryGetValue(chatId, out var userReminders))
                        {
                            userReminders = new();
                            _userReminders[chatId] = userReminders;
                        }

                        userReminders[product] = reminderTime;
                        SaveUserData();

                        await _botClient.SendTextMessageAsync(
                            chatId: chatId,
                            text: $"Напоминание для \"{product}\" установлено на {reminderTime:HH:mm}!",
                            replyMarkup: GetMainMenuKeyboard(),
                            cancellationToken: cancellationToken);
                    }
                    else
                    {
                        await _botClient.SendTextMessageAsync(
                            chatId: chatId,
                            text: "Неверный формат времени. Используйте формат ЧЧ:ММ (например, 09:00 или 21:30):",
                            replyMarkup: new ReplyKeyboardRemove(),
                            cancellationToken: cancellationToken);
                        return;
                    }
                    userState.CurrentAction = UserAction.None;
                    userState.TempProductForReminder = null;
                    break;

                case UserAction.RemovingReminder:
                    if (_userReminders.TryGetValue(chatId, out var userRemindersList) &&
                        int.TryParse(messageText, out var reminderIndex) &&
                        reminderIndex > 0 && reminderIndex <= userRemindersList.Count)
                    {
                        var reminder = userRemindersList.ElementAt(reminderIndex - 1);
                        userRemindersList.Remove(reminder.Key);
                        SaveUserData();

                        await _botClient.SendTextMessageAsync(
                            chatId: chatId,
                            text: $"Напоминание для \"{reminder.Key}\" удалено!",
                            replyMarkup: GetMainMenuKeyboard(),
                            cancellationToken: cancellationToken);
                    }
                    else
                    {
                        await _botClient.SendTextMessageAsync(
                            chatId: chatId,
                            text: "Неверный номер напоминания. Пожалуйста, попробуйте еще раз.",
                            replyMarkup: GetRemindersListKeyboard(_userReminders[chatId]),
                            cancellationToken: cancellationToken);
                        return;
                    }
                    userState.CurrentAction = UserAction.None;
                    break;

                default:
                    await SendStartMessage(chatId, cancellationToken);
                    break;
            }
        }

        private static string GetTimeCategoryName(TimeCategory category)
        {
            return category switch
            {
                TimeCategory.Morning => "Утро",
                TimeCategory.BeforeMakeup => "Перед макияжем",
                TimeCategory.Evening => "Вечер/Перед сном",
                TimeCategory.Anytime => "Любое время",
                _ => "Не указано"
            };
        }

        private static async Task SendStartMessage(long chatId, CancellationToken cancellationToken)
        {
            var message = "🌟 Добро пожаловать в бот для ухода за кожей лица! 🌟\n\n" +
                          "Я помогу вам:\n" +
                          "• Вести список косметических средств\n" +
                          "• Организовывать средства по времени использования\n" +
                          "• Отслеживать основные компоненты и их назначение\n" +
                          "• Напоминать о времени ухода за кожей\n\n" +
                          "Выберите действие:";

            await _botClient.SendTextMessageAsync(
                chatId: chatId,
                text: message,
                replyMarkup: GetMainMenuKeyboard(),
                cancellationToken: cancellationToken);
        }

        private static async Task ShowProductsList(long chatId, CancellationToken cancellationToken)
        {
            if (!_userStates.TryGetValue(chatId, out var userState) || userState.Products.Count == 0)
            {
                await _botClient.SendTextMessageAsync(
                    chatId: chatId,
                    text: "Ваш список средств пуст.",
                    replyMarkup: GetMainMenuKeyboard(),
                    cancellationToken: cancellationToken);
                return;
            }

            var morningProducts = userState.Products
                .Where(p => p.TimeCategory == TimeCategory.Morning)
                .Select((p, i) => $"{i + 1}. {p.Name} ({p.MainComponent} - {p.Purpose})");

            var makeupProducts = userState.Products
                .Where(p => p.TimeCategory == TimeCategory.BeforeMakeup)
                .Select((p, i) => $"{i + 1}. {p.Name} ({p.MainComponent} - {p.Purpose})");

            var eveningProducts = userState.Products
                .Where(p => p.TimeCategory == TimeCategory.Evening)
                .Select((p, i) => $"{i + 1}. {p.Name} ({p.MainComponent} - {p.Purpose})");

            var anytimeProducts = userState.Products
                .Where(p => p.TimeCategory == TimeCategory.Anytime)
                .Select((p, i) => $"{i + 1}. {p.Name} ({p.MainComponent} - {p.Purpose})");

            var message = "Ваши косметические средства:\n\n" +
                         "🌅 Утренний уход:\n" + (morningProducts.Any() ? string.Join("\n", morningProducts) : "Нет средств") + "\n\n" +
                         "💄 Перед макияжем:\n" + (makeupProducts.Any() ? string.Join("\n", makeupProducts) : "Нет средств") + "\n\n" +
                         "🌙 Вечерний уход:\n" + (eveningProducts.Any() ? string.Join("\n", eveningProducts) : "Нет средств") + "\n\n" +
                         "⏰ Любое время:\n" + (anytimeProducts.Any() ? string.Join("\n", anytimeProducts) : "Нет средств");

            await _botClient.SendTextMessageAsync(
                chatId: chatId,
                text: message,
                replyMarkup: GetMainMenuKeyboard(),
                cancellationToken: cancellationToken);
        }

        private static async Task ShowProductsListWithNumbers(long chatId, CancellationToken cancellationToken)
        {
            if (!_userStates.TryGetValue(chatId, out var userState)) return;

            var message = "Выберите средство по номеру:\n\n" +
                         string.Join("\n", userState.Products.Select((p, i) => $"{i + 1}. {p.Name} ({GetTimeCategoryName(p.TimeCategory)})"));

            await _botClient.SendTextMessageAsync(
                chatId: chatId,
                text: message,
                replyMarkup: GetProductsListKeyboard(userState.Products),
                cancellationToken: cancellationToken);
        }

        private static async Task ShowRemindersList(long chatId, CancellationToken cancellationToken)
        {
            if (!_userReminders.TryGetValue(chatId, out var reminders) || reminders.Count == 0)
            {
                await _botClient.SendTextMessageAsync(
                    chatId: chatId,
                    text: "У вас нет активных напоминаний.",
                    replyMarkup: GetMainMenuKeyboard(),
                    cancellationToken: cancellationToken);
                return;
            }

            var message = "Ваши напоминания:\n\n" +
                         string.Join("\n", reminders.Select((r, i) => $"{i + 1}. {r.Key} - {r.Value:HH:mm}"));

            await _botClient.SendTextMessageAsync(
                chatId: chatId,
                text: message,
                replyMarkup: GetMainMenuKeyboard(),
                cancellationToken: cancellationToken);
        }

        private static async Task ShowRemindersListWithNumbers(long chatId, CancellationToken cancellationToken)
        {
            if (!_userReminders.TryGetValue(chatId, out var reminders)) return;

            var message = "Выберите напоминание по номеру:\n\n" +
                         string.Join("\n", reminders.Select((r, i) => $"{i + 1}. {r.Key} - {r.Value:HH:mm}"));

            await _botClient.SendTextMessageAsync(
                chatId: chatId,
                text: message,
                replyMarkup: GetRemindersListKeyboard(reminders),
                cancellationToken: cancellationToken);
        }

        private static ReplyKeyboardMarkup GetMainMenuKeyboard()
        {
            return new(new[]
            {
                new KeyboardButton[] { "Мои средства", "Добавить средство" },
                new KeyboardButton[] { "Удалить средство", "Добавить напоминание" },
                new KeyboardButton[] { "Мои напоминания", "Удалить напоминание" },
                new KeyboardButton[] { "Начать с начала", "Перезапустить бота" }
            })
            {
                ResizeKeyboard = true
            };
        }

        private static ReplyKeyboardMarkup GetTimeCategoryKeyboard()
        {
            return new(new[]
            {
                new KeyboardButton[] { TimeCategory.Morning.ToString() },
                new KeyboardButton[] { TimeCategory.BeforeMakeup.ToString() },
                new KeyboardButton[] { TimeCategory.Evening.ToString() },
                new KeyboardButton[] { TimeCategory.Anytime.ToString() }
            })
            {
                ResizeKeyboard = true
            };
        }

        private static ReplyKeyboardMarkup GetProductsListKeyboard(List<CosmeticProduct> products)
        {
            var buttons = products.Select((_, i) => new KeyboardButton((i + 1).ToString())).ToArray();
            return new(buttons) { ResizeKeyboard = true };
        }

        private static ReplyKeyboardMarkup GetRemindersListKeyboard(Dictionary<string, DateTime> reminders)
        {
            var buttons = reminders.Select((_, i) => new KeyboardButton((i + 1).ToString())).ToArray();
            return new(buttons) { ResizeKeyboard = true };
        }

        private static Task HandleErrorAsync(ITelegramBotClient _, Exception exception, CancellationToken __)
        {
            Console.WriteLine($"Ошибка: {exception.Message}");
            return Task.CompletedTask;
        }
    }

    public class UserState
    {
        public List<CosmeticProduct> Products { get; set; } = new();
        public UserAction CurrentAction { get; set; } = UserAction.None;
        public CosmeticProduct? TempProduct { get; set; }
        public string? TempProductForReminder { get; set; }
    }

    public class CosmeticProduct
    {
        public string Name { get; set; } = string.Empty;
        public TimeCategory TimeCategory { get; set; } = TimeCategory.Anytime;
        public string MainComponent { get; set; } = string.Empty;
        public string Purpose { get; set; } = string.Empty;
    }

    public enum UserAction
    {
        None,
        AddingProduct,
        SelectingTimeCategory,
        AddingComponent,
        RemovingProduct,
        AddingReminder,
        SettingReminderTime,
        RemovingReminder
    }
}