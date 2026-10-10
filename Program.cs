using System;
using System.Collections.Concurrent;
using System.Net;
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
    public enum RegistrationStep
    {
        None,
        AwaitingName,
        AwaitingPhone,
        AwaitingTeamName
    }

    public class UserState
    {
        public string Language { get; set; } = "ru";
        public RegistrationStep Step { get; set; } = RegistrationStep.None;
        public string FullName { get; set; } = "";
        public string PhoneNumber { get; set: } = "";
        public string TeamName { get; set; } = "";
    }

    class Program
    {
        private static readonly ConcurrentDictionary<long, UserState> Users = new();
        
        // Твой личный Chat ID для уведомлений о новых регистрациях
        private static readonly long AdminChatId = 8791726949; 

        static async Task Main(string[] args)
        {
            // Открываем порт для Render Web Service
            try
            {
                var listener = new HttpListener();
                int port = int.Parse(Environment.GetEnvironmentVariable("PORT") ?? "8080");
                listener.Prefixes.Add($"http://*:{port}/");
                listener.Start();
                Task.Run(async () => { while (true) { var ctx = await listener.GetContextAsync(); ctx.Response.Close(); } });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Порт не запущен (это нормально для ПК): {ex.Message}");
            }

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
                HandleUpdateAsync,
                HandlePollingErrorAsync,
                receiverOptions,
                cts.Token
            );

            Console.WriteLine("Бот успешно запущен и работает!");
            await Task.Delay(-1, cts.Token);
        }

        private static async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
        {
            if (update.Message is not { } message || message.Text is not { } messageText)
                return;

            long chatId = message.Chat.Id;
            var user = Users.GetOrAdd(chatId, _ => new UserState());

            if (messageText == "/start" || messageText == "🌐 Выбрать язык / Tilingizni tanlang / Choose language")
            {
                user.Step = RegistrationStep.None;
                await SendLanguageMenu(botClient, chatId, cancellationToken);
                return;
            }

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

            if (messageText is "Регистрация" or "Ro'yxatdan o'tish" or "Registration")
            {
                user.Step = RegistrationStep.AwaitingName;
                string prompt = user.Language switch
                {
                    "uz" => "Исм ва фамилиянгизни киритинг (масалан: Иван Иванов):",
                    "en" => "Please enter your Name and Surname (e.g. John Doe):",
                    _ => "Введите ваше Имя и Фамилию (например: Иван Иванов):"
                };
                await botClient.SendMessage(chatId, prompt, replyMarkup: new ReplyKeyboardRemove(), cancellationToken: cancellationToken);
                return;
            }

            if (messageText is "Помощь" or "Yordam" or "Help")
            {
                string helpText = user.Language switch
                {
                    "uz" => "📞 Yordam:\nTelefon: +998 90 379 29 39\nTelegram: @ronchece",
                    "en" => "📞 Help:\nPhone: +998 90 379 29 39\nTelegram: @ronchece",
                    _ => "📞 Помощь:\nТелефон: +998 90 379 29 39\nTelegram: @ronchece"
                };
                await botClient.SendMessage(chatId, helpText, cancellationToken: cancellationToken);
                return;
            }

            // Обработка кнопки "Мы в социальной сети" на всех языках
            if (messageText is "📱 Мы в социальной сети" or "📱 Ijtimoiy tarmoqlardamiz" or "📱 Social Networks")
            {
                await SendSocialMediaMenu(botClient, chatId, user, cancellationToken);
                return;
            }

            switch (user.Step)
            {
                case RegistrationStep.AwaitingName:
                    user.FullName = messageText;
                    user.Step = RegistrationStep.AwaitingPhone;
                    string phonePrompt = user.Language switch
                    {
                        "uz" => "Телефон рақамингизни шу форматда киритинг: +998 хх ххх хх хх",
                        "en" => "Enter your phone number in the format: +998 xx xxx xx xx",
                        _ => "Введите номер телефона в формате: +998 хх ххх хх хх"
                    };
                    await botClient.SendMessage(chatId, phonePrompt, cancellationToken: cancellationToken);
                    break;

                case RegistrationStep.AwaitingPhone:
                    if (!Regex.IsMatch(messageText, @"^\+998\s?\d{2}\s?\d{3}\s?\d{2}\s?\d{2}$"))
                    {
                        string errorPhone = user.Language switch
                        {
                            "uz" => "Формат нотўғри! Илтимос, рақамни +998 хх ххх хх хх форматида киритинг:",
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

                    // 1. Уведомление участнику
                    string successMsg = user.Language switch
                    {
                        "uz" => $"🎉 Муваффақиятли рўйхатдан ўтдингиз!\n\n👤 Исм: {user.FullName}\n📞 Рақам: {user.PhoneNumber}\n👥 Жамоа: {user.TeamName}",
                        "en" => $"🎉 Registration successful!\n\n👤 Name: {user.FullName}\n📞 Phone: {user.PhoneNumber}\n👥 Team: {user.TeamName}",
                        _ => $"🎉 Вы успешно зарегистрировались!\n\n👤 Имя: {user.FullName}\n📞 Номер: {user.PhoneNumber}\n👥 Команда: {user.TeamName}"
                    };
                    await botClient.SendMessage(chatId, successMsg, cancellationToken: cancellationToken);

                    // 2. Отправка отчета администратору
                    if (AdminChatId != 0)
                    {
                        string adminNotification = $"🚨 **Новый участник зарегистрировался!**\n\n👤 Имя: {user.FullName}\n📞 Телефон: {user.PhoneNumber}\n👥 Команда: {user.TeamName}\n💬 Telegram ID: `{chatId}`";
                        await botClient.SendMessage(AdminChatId, adminNotification, parseMode: Telegram.Bot.Types.Enums.ParseMode.Markdown, cancellationToken: cancellationToken);
                    }

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
            string socialBtn = user.Language switch { "uz" => "📱 Ijtimoiy tarmoqlardamiz", "en" => "📱 Social Networks", _ => "📱 Мы в социальной сети" };

            var keyboard = new ReplyKeyboardMarkup(new[]
            {
                new KeyboardButton[] { regBtn, helpBtn },
                new KeyboardButton[] { socialBtn },
                new KeyboardButton[] { "🌐 Выбрать язык / Tilingizni tanlang / Choose language" }
            })
            {
                ResizeKeyboard = true
            };

            string text = user.Language switch
            {
                "uz" => "Асосий меню. Керакли бўлимни танланг:",
                "en" => "Main Menu. Choose an option:",
                _ => "Главное меню. Выберите нужный раздел:"
            };

            await botClient.SendMessage(chatId, text, replyMarkup: keyboard, cancellationToken: cancellationToken);
        }

        private static async Task SendSocialMediaMenu(ITelegramBotClient botClient, long chatId, UserState user, CancellationToken cancellationToken)
        {
            string tg1Text = user.Language switch { "uz" => "📢 Telegram 1-guruh", "en" => "📢 Telegram Group 1", _ => "📢 Telegram 1-группа" };
            string tg2Text = user.Language switch { "uz" => "📢 Telegram 2-guruh", "en" => "📢 Telegram Group 2", _ => "📢 Telegram 2-группа" };
            string instaText = user.Language switch { "uz" => "📸 Instagram sahifamiz", "en" => "📸 Instagram Page", _ => "📸 Instagram аккаунт" };

            string subscribeText = user.Language switch
            {
                "uz" => "🔗 Obuna bo'lish:",
                "en" => "🔗 Subscribe:",
                _ => "🔗 Подписаться:"
            };

            var inlineKeyboard = new InlineKeyboardMarkup(new[]
            {
                new[] { InlineKeyboardButton.WithUrl($"{tg1Text} ({subscribeText})", "https://t.me/yosh_ekologlarharakati") },
                new[] { InlineKeyboardButton.WithUrl($"{tg2Text} ({subscribeText})", "https://t.me/ekologlar_kr") },
                new[] { InlineKeyboardButton.WithUrl($"{instaText} ({subscribeText})", "https://www.instagram.com/ekologlar_hareketi_qr?vrfl=emh1cW93Z21ibm54") }
            });

            string menuTitle = user.Language switch
            {
                "uz" => "Bizning ijtimoiy tarmoqlarimizga obuna bo'ling:",
                "en" => "Subscribe to our social networks:",
                _ => "Подписывайтесь на наши социальные сети:"
            };

            await botClient.SendMessage(chatId, menuTitle, replyMarkup: inlineKeyboard, cancellationToken: cancellationToken);
        }

        private static Task HandlePollingErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
        {
            Console.WriteLine(exception.ToString());
            return Task.CompletedTask;
        }
    }
}