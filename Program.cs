using System;
using System;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;

namespace TelegramBot
{
    // Шаги регистрации
    public enum RegistrationStep
    {
        None,
        AwaitingName,
        AwaitingPhone,
        AwaitingTeamName
    }

    // Состояние пользователя
    public class UserState
    {
        public string Language { get; set; } = "ru"; // "ru", "uz", "en"
        public RegistrationStep Step { get; set; } = RegistrationStep.None;
        public string FullName { get; set; } = "";
        public string PhoneNumber { get; set; } = "";
        public string TeamName { get; set; } = "";
    }

    class Program
    {
        private static readonly ConcurrentDictionary<long, UserState> Users = new();

        static async Task Main(string[] args)
        {
            var token = Environment.GetEnvironmentVariable("BOT_TOKEN");
            if (string.IsNullOrEmpty(token))
            {
                Console.WriteLine("Ошибка: Переменная BOT_TOKEN не найдена!");
                return;
            }

            var botClient = new TelegramBotClient(token);
            using var cts = new CancellationTokenSource();

            var receiverOptions = new ReceiverOptions
            {
                AllowedUpdates = Array.Empty<Telegram.Bot.Types.Enums.UpdateType>()
            };

            botClient.StartReceiving(
                updateHandler: HandleUpdateAsync,
                pollingErrorHandler: HandlePollingErrorAsync,
                receiverOptions: receiverOptions,
                cancellationToken: cts.Token
            );

            Console.WriteLine("Бот успешно запущен и работает!");

            // Зависаем в бесконечном ожидании, чтобы контейнер в Render НЕ завершал работу
            await Task.Delay(-1, cts.Token);
        }

        private static async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
        {
            if (update.Message is not { } message || message.Text is not { } messageText)
                return;

            long chatId = message.Chat.Id;
            var user = Users.GetOrAdd(chatId, _ => new UserState());

            // Обработка стартовой команды /start или смены языка
            if (messageText == "/start" || messageText == "🌐 Выбрать язык / Tilingizni tanlang / Choose language")
            {
                user.Step = RegistrationStep.None;
                await SendLanguageMenu(botClient, chatId, cancellationToken);
                return;
            }

            // Выбор языка
            if (messageText is "Русский 🇷🇺" or "O'zbekcha 🇺🇿" or "English 🇬🇧")
            {
                user.Language = messageText switch
                {
                    "O'zbekcha 🇺🇿" => "uz",
                    "English 🇬🇧" => "en",
                    _ => "ru"
                };
                user.Step = RegistrationStep.None;
                await SendMainMenu(botClient, chatId, user, cancellationToken);
                return;
            }

            // Обработка кнопки Регистрация
            if (messageText is "Регистрация" or "Ro'yxatdan o'tish" or "Registration")
            {
                user.Step = RegistrationStep.AwaitingName;
                string prompt = user.Language switch
                {
                    "uz" => "Имя и фамилия киргизинг (масалан: Иван Иванов):",
                    "en" => "Please enter your Name and Surname (e.g. John Doe):",
                    _ => "Введите ваше Имя и Фамилию (например: Иван Иванов):"
                };
                await botClient.SendMessage(chatId, prompt, replyMarkup: new ReplyKeyboardRemove(), cancellationToken: cancellationToken);
                return;
            }

            // Обработка кнопки Помощь
            if (messageText is "Помощь" or "Yordam" or "Help")
            {
                string helpText = user.Language switch
                {
                    "uz" => "📞 Помощь / Yordam:\nТелефон: +998 90 379 29 39\nTelegram: @ronchece",
                    "en" => "📞 Help:\nPhone: +998 90 379 29 39\nTelegram: @ronchece",
                    _ => "📞 Помощь:\nТелефон: +998 90 379 29 39\nTelegram: @ronchece"
                };
                await botClient.SendMessage(chatId, helpText, cancellationToken: cancellationToken);
                return;
            }

            // Пошаговый процесс регистрации
            switch (user.Step)
            {
                case RegistrationStep.AwaitingName:
                    user.FullName = messageText;
                    user.Step = RegistrationStep.AwaitingPhone;
                    string phonePrompt = user.Language switch
                    {
                        "uz" => "Телефон рақамингизни шаклда киритинг: +998 xx xxx xx xx",
                        "en" => "Enter your phone number in the format: +998 xx xxx xx xx",
                        _ => "Введите номер телефона в формате: +998 хх ххх хх хх"
                    };
                    await botClient.SendMessage(chatId, phonePrompt, cancellationToken: cancellationToken);
                    break;

                case RegistrationStep.AwaitingPhone:
                    // Простая проверка формата телефона
                    if (!Regex.IsMatch(messageText, @"^\+998\s?\d{2}\s?\d{3}\s?\d{2}\s?\d{2}$"))
                    {
                        string errorPhone = user.Language switch
                        {
                            "uz" => "Формат нотоғри! Илтимос, номерни +998 хх ххх хх хх форматида киритинг:",
                            "en" => "Invalid format! Please enter phone number in format +998 xx xxx xx xx:",
                            _ => "Неверный формат! Введите номер строго в формате +998 хх ххх хх хх:"
                        };
                        await botClient.SendMessage(chatId, errorPhone, cancellationToken: cancellationToken);
                        return;
                    }

                    user.PhoneNumber = messageText;
                    user.Step = RegistrationStep.AwaitingTeamName;
                    string teamPrompt = user.Language switch
                    {
                        "uz" => "Жамоангиз номини киритинг:",
                        "en" => "Enter your team name:",
                        _ => "Напишите название для своей команды:"
                    };
                    await botClient.SendMessage(chatId, teamPrompt, cancellationToken: cancellationToken);
                    break;

                case RegistrationStep.AwaitingTeamName:
                    user.TeamName = messageText;
                    user.Step = RegistrationStep.None;

                    string successMsg = user.Language switch
                    {
                        "uz" => $"🎉 Ro'yxatdan o'tdingiz!\n\n👤 Исм: {user.FullName}\n📞 Номер: {user.PhoneNumber}\n👥 Жамоа: {user.TeamName}",
                        "en" => $"🎉 Registration successful!\n\n👤 Name: {user.FullName}\n📞 Phone: {user.PhoneNumber}\n👥 Team: {user.TeamName}",
                        _ => $"🎉 Вы успешно зарегистрировались!\n\n👤 Имя: {user.FullName}\n📞 Номер: {user.PhoneNumber}\n👥 Команда: {user.TeamName}"
                    };

                    await botClient.SendMessage(chatId, successMsg, cancellationToken: cancellationToken);
                    await SendMainMenu(botClient, chatId, user, cancellationToken);
                    break;
            }
        }

        private static async Task SendLanguageMenu(ITelegramBotClient botClient, long chatId, CancellationToken cancellationToken)
        {
            var keyboard = new ReplyKeyboardMarkup(new[]
            {
                new KeyboardButton[] { "Русский 🇷🇺", "O'zbekcha 🇺🇿", "English 🇬🇧" }
            })
            {
                ResizeKeyboard = true
            };

            await botClient.SendMessage(chatId, "Выберите язык / Tilingizni tanlang / Choose your language:", replyMarkup: keyboard, cancellationToken: cancellationToken);
        }

        private static async Task SendMainMenu(ITelegramBotClient botClient, long chatId, UserState user, CancellationToken cancellationToken)
        {
            string regBtn = user.Language switch { "uz" => "Ro'yxatdan o'tish", "en" => "Registration", _ => "Регистрация" };
            string helpBtn = user.Language switch { "uz" => "Yordam", "en" => "Help", _ => "Помощь" };

            var keyboard = new ReplyKeyboardMarkup(new[]
            {
                new KeyboardButton[] { regBtn, helpBtn },
                new KeyboardButton[] { "🌐 Выбрать язык / Tilingizni tanlang / Choose language" }
            })
            {
                ResizeKeyboard = true
            };

            string text = user.Language switch
            {
                "uz" => "Бош меню. Кракли бўлимни танланг:",
                "en" => "Main Menu. Choose an option:",
                _ => "Главное меню. Выберите нужный раздел:"
            };

            await botClient.SendMessage(chatId, text, replyMarkup: keyboard, cancellationToken: cancellationToken);
        }

        private static Task HandlePollingErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
        {
            var errorMessage = exception switch
            {
                ApiRequestException apiRequestException => $"Telegram API Error:\n[{apiRequestException.ErrorCode}]\n{apiRequestException.Message}",
                _ => exception.ToString()
            };

            Console.WriteLine(errorMessage);
            return Task.CompletedTask;
        }
    }
}