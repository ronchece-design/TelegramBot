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
using Telegram.Bot.Types.Enums;
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
        public string PhoneNumber { get; set; } = "";
        public string TeamName { get; set; } = "";
    }

    class Program
    {
        private static readonly ConcurrentDictionary<long, UserState> Users = new();
        
        // Твой личный Chat ID для уведомлений
        private static readonly long AdminChatId = 8791726949; 

        // Единственный Telegram-канал для обязательной проверки подписки
        private static readonly string RequiredChannel = "@ekologlar_kr";

        static async Task Main(string[] args)
        {
            try
            {
                var listener = new HttpListener();
                int port = int.Parse(Environment.GetEnvironmentVariable("PORT") ?? "8080");
                listener.Prefixes.Add($"http://*:{port}/");
                listener.Start();
                _ = Task.Run(async () => { 
                    while (true) 
                    { 
                        try 
                        { 
                            var ctx = await listener.GetContextAsync(); 
                            ctx.Response.Close(); 
                        } 
                        catch { } 
                    } 
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Порт не запущен: {ex.Message}");
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
                AllowedUpdates = Array.Empty<UpdateType>()
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
            // Проверка нажатия на кнопку проверки подписки
            if (update.CallbackQuery is { } callbackQuery)
            {
                long chatId = callbackQuery.Message!.Chat.Id;
                var user = Users.GetOrAdd(chatId, _ => new UserState());

                if (callbackQuery.Data == "check_subscription")
                {
                    bool isSubscribed = false;
                    try
                    {
                        var member = await botClient.GetChatMember(RequiredChannel, chatId, cancellationToken);
                        if (member.Status is ChatMemberStatus.Member or ChatMemberStatus.Administrator or ChatMemberStatus.Creator)
                        {
                            isSubscribed = true;
                        }
                    }
                    catch
                    {
                        isSubscribed = true; // Если бот не админ в канале, пропускаем
                    }

                    if (isSubscribed)
                    {
                        await botClient.AnswerCallbackQuery(callbackQuery.Id, user.Language switch
                        {
                            "uz" => "✅ Obuna tasdiqlandi!",
                            "en" => "✅ Subscription confirmed!",
                            _ => "✅ Подписка подтверждена!"
                        }, cancellationToken: cancellationToken);

                        user.Step = RegistrationStep.AwaitingName;
                        string prompt = user.Language switch
                        {
                            "uz" => "Исм ва фамилиянгизни киритинг (масалан: Иван Иванов):",
                            "en" => "Please enter your Name and Surname (e.g. John Doe):",
                            _ => "Введите ваше Имя и Фамилию (например: Иван Иванов):"
                        };
                        await botClient.SendMessage(chatId, prompt, replyMarkup: new ReplyKeyboardRemove(), cancellationToken: cancellationToken);
                    }
                    else
                    {
                        await botClient.AnswerCallbackQuery(callbackQuery.Id, user.Language switch
                        {
                            "uz" => "❌ Iltimos, avval kanalga obuna bo'ling!",
                            "en" => "❌ Please subscribe to the channel first!",
                            _ => "❌ Пожалуйста, подпишитесь на канал!"
                        }, showAlert: true, cancellationToken: cancellationToken);
                    }
                }
                return;
            }

            if (update.Message is not { } message || message.Text is not { } messageText)
                return;

            long chatIdMsg = message.Chat.Id;
            var userState = Users.GetOrAdd(chatIdMsg, _ => new UserState());

            if (messageText == "/start" || messageText == "🌐 Выбрать язык / Tilingizni tanlang / Choose language")
            {
                userState.Step = RegistrationStep.None;
                await SendLanguageMenu(botClient, chatIdMsg, cancellationToken);
                return;
            }

            if (messageText is "Русский 🇷🇺" or "O'zbekcha 🇺🇿" or "English 🇬🇧")
            {
                userState.Language = messageText switch
                {
                    "O'zbekcha 🇺🇿" => "uz",
                    "English 🇬🇧" => "en",
                    _ => "ru"
                };
                userState.Step = RegistrationStep.None;
                await SendMainMenu(botClient, chatIdMsg, userState, cancellationToken);
                return;
            }

            if (messageText is "Регистрация" or "Ro'yxatdan o'tish" or "Registration")
            {
                await SendSubscriptionPrompt(botClient, chatIdMsg, userState, cancellationToken);
                return;
            }

            if (messageText is "Помощь" or "Yordam" or "Help")
            {
                string helpText = userState.Language switch
                {
                    "uz" => "📞 Yordam:\nTelefon: +998 90 379 29 39\nTelegram: @ronchece",
                    "en" => "📞 Help:\nPhone: +998 90 379 29 39\nTelegram: @ronchece",
                    _ => "📞 Помощь:\nТелефон: +998 90 379 29 39\nTelegram: @ronchece"
                };
                await botClient.SendMessage(chatIdMsg, helpText, cancellationToken: cancellationToken);
                return;
            }

            if (messageText is "📱 Мы в социальной сети" or "📱 Ijtimoiy tarmoqlardamiz" or "📱 Social Networks")
            {
                await SendSocialMediaMenu(botClient, chatIdMsg, userState, cancellationToken);
                return;
            }

            switch (userState.Step)
            {
                case RegistrationStep.AwaitingName:
                    userState.FullName = messageText;
                    userState.Step = RegistrationStep.AwaitingPhone;
                    string phonePrompt = userState.Language switch
                    {
                        "uz" => "Телефон рақамингизни шу форматда киритинг: +998 хх ххх хх хх",
                        "en" => "Enter your phone number in the format: +998 xx xxx xx xx",
                        _ => "Введите номер телефона в формате: +998 хх ххх хх хх"
                    };
                    await botClient.SendMessage(chatIdMsg, phonePrompt, cancellationToken: cancellationToken);
                    break;

                case RegistrationStep.AwaitingPhone:
                    if (!Regex.IsMatch(messageText, @"^\+998\s?\d{2}\s?\d{3}\s?\d{2}\s?\d{2}$"))
                    {
                        string errorPhone = userState.Language switch
                        {
                            "uz" => "Формат нотўғри! Илтимос, рақамни +998 хх ххх хх хх форматида киритинг:",
                            "en" => "Invalid format! Please enter phone number in format +998 xx xxx xx xx:",
                            _ => "Неверный формат! Введите номер строго в формате +998 хх ххх хх хх:"
                        };
                        await botClient.SendMessage(chatIdMsg, errorPhone, cancellationToken: cancellationToken);
                        return;
                    }

                    userState.PhoneNumber = messageText;
                    userState.Step = RegistrationStep.AwaitingTeamName;
                    string teamPrompt = userState.Language switch
                    {
                        "uz" => "Жамоангиз номини киритинг:",
                        "en" => "Enter your team name:",
                        _ => "Напишите название для своей команды:"
                    };
                    await botClient.SendMessage(chatIdMsg, teamPrompt, cancellationToken: cancellationToken);
                    break;

                case RegistrationStep.AwaitingTeamName:
                    userState.TeamName = messageText;
                    userState.Step = RegistrationStep.None;

                    string successMsg = userState.Language switch
                    {
                        "uz" => $"🎉 Муваффақиятли рўйхатдан ўтдингиз!\n\n👤 Исм: {userState.FullName}\n📞 Рақам: {userState.PhoneNumber}\n👥 Жамоа: {userState.TeamName}",
                        "en" => $"🎉 Registration successful!\n\n👤 Name: {userState.FullName}\n📞 Phone: {userState.PhoneNumber}\n👥 Team: {userState.TeamName}",
                        _ => $"🎉 Вы успешно зарегистрировались!\n\n👤 Имя: {userState.FullName}\n📞 Номер: {userState.PhoneNumber}\n👥 Команда: {userState.TeamName}"
                    };
                    await botClient.SendMessage(chatIdMsg, successMsg, cancellationToken: cancellationToken);

                    if (AdminChatId != 0)
                    {
                        try
                        {
                            string adminNotification = $"🚨 **Новый участник зарегистрировался!**\n\n👤 Имя: {userState.FullName}\n📞 Телефон: {userState.PhoneNumber}\n👥 Команда: {userState.TeamName}\n💬 Telegram ID: `{chatIdMsg}`";
                            await botClient.SendMessage(AdminChatId, adminNotification, parseMode: ParseMode.Markdown, cancellationToken: cancellationToken);
                        }
                        catch { }
                    }

                    await SendMainMenu(botClient, chatIdMsg, userState, cancellationToken);
                    break;
            }
        }

        private static async Task SendSubscriptionPrompt(ITelegramBotClient botClient, long chatId, UserState user, CancellationToken cancellationToken)
        {
            string text = user.Language switch
            {
                "uz" => "Ro'yxatdan o'tish uchun quyidagi kanal va sahifaga obuna bo'ling:",
                "en" => "To register, please subscribe to our channel and page:",
                _ => "Для регистрации подпишитесь на наш канал и страницу:"
            };

            var keyboard = new InlineKeyboardMarkup(new[]
            {
                new[] { InlineKeyboardButton.WithUrl("📢 Telegram канал", "https://t.me/ekologlar_kr") },
                new[] { InlineKeyboardButton.WithUrl("📸 Instagram", "https://www.instagram.com/ekologlar_hareketi_qr?vrfl=emh1cW93Z21ibm54") },
                new[] { InlineKeyboardButton.WithCallbackData(user.Language == "uz" ? "✅ Obunani tekshirish" : user.Language == "en" ? "✅ Check Subscription" : "✅ Проверить подписку", "check_subscription") }
            });

            await botClient.SendMessage(chatId, text, replyMarkup: keyboard, cancellationToken: cancellationToken);
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
            string tgText = user.Language switch { "uz" => "📢 Telegram kanal", "en" => "📢 Telegram Channel", _ => "📢 Telegram канал" };
            string instaText = user.Language switch { "uz" => "📸 Instagram sahifa", "en" => "📸 Instagram Page", _ => "📸 Instagram аккаунт" };

            var inlineKeyboard = new InlineKeyboardMarkup(new[]
            {
                new[] { InlineKeyboardButton.WithUrl(tgText, "https://t.me/ekologlar_kr") },
                new[] { InlineKeyboardButton.WithUrl(instaText, "https://www.instagram.com/ekologlar_hareketi_qr?vrfl=emh1cW93Z21ibm54") }
            });

            string menuTitle = user.Language switch
            {
                "uz" => "Bizning ijtimoiy tarmoqlarimiz:",
                "en" => "Our social networks:",
                _ => "Наши социальные сети:"
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