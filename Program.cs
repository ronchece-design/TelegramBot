using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
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
        AwaitingTeamName,
        AwaitingSecretCode
    }

    public class UserState
    {
        public long ChatId { get; set; }
        public string Language { get; set; } = "ru";
        public RegistrationStep Step { get; set; } = RegistrationStep.None;
        public string FullName { get; set; } = "";
        public string PhoneNumber { get; set; } = "";
        public string TeamId { get; set; } = "";
        public string PendingTeamId { get; set; } = ""; // Запоминаем команду при переходе по ссылке или коду
        public bool IsLeader { get; set; } = false;
    }

    public class TeamModel
    {
        public string TeamId { get; set; } = "";
        public string SecretCode { get; set; } = "";
        public string Name { get; set; } = "";
        public long LeaderChatId { get; set; }
        public List<long> Members { get; set; } = new();
    }

    class Program
    {
        private static readonly ConcurrentDictionary<long, UserState> Users = new();
        private static readonly ConcurrentDictionary<string, TeamModel> TeamsByCode = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, TeamModel> TeamsById = new(StringComparer.OrdinalIgnoreCase);
        
        // Твой личный Chat ID для уведомлений
        private static readonly long AdminChatId = 8791726949; 

        // Канал для обязательной проверки подписки
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
            // Проверка кнопки "Проверить подписку"
            if (update.CallbackQuery is { } callbackQuery)
            {
                long chatId = callbackQuery.Message!.Chat.Id;
                var user = Users.GetOrAdd(chatId, _ => new UserState { ChatId = chatId });

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
                        isSubscribed = true; // Если бот не админ, пропускаем
                    }

                    if (isSubscribed)
                    {
                        await botClient.AnswerCallbackQuery(callbackQuery.Id, user.Language switch
                        {
                            "qr" => "✅ Agza bolg'anlig'in'iz tastıyklandı!",
                            "uz" => "✅ Obuna tasdiqlandi!",
                            "en" => "✅ Subscription confirmed!",
                            _ => "✅ Подписка подтверждена!"
                        }, cancellationToken: cancellationToken);

                        // Переходим к вводу имени
                        user.Step = RegistrationStep.AwaitingName;
                        string prompt = user.Language switch
                        {
                            "qr" => "Atıńız ha'm familiyańızdı kiritiń (mısalı: Ivan Ivanov):",
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
                            "qr" => "❌ Iltimos, aldın kanalǵa agza bolıń!",
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
            var userState = Users.GetOrAdd(chatIdMsg, _ => new UserState { ChatId = chatIdMsg });

            // Обработка /start с аргументами ссылки
            if (messageText.StartsWith("/start"))
            {
                userState.Step = RegistrationStep.None;
                var parts = messageText.Split(' ');
                if (parts.Length > 1 && parts[1].StartsWith("team_"))
                {
                    string targetTeamId = parts[1].Replace("team_", "");
                    if (TeamsById.ContainsKey(targetTeamId))
                    {
                        userState.PendingTeamId = targetTeamId;
                    }
                }
                await SendLanguageMenu(botClient, chatIdMsg, cancellationToken);
                return;
            }

            if (messageText == "🌐 Выбрать язык / Tilingizni tanlang / Choose language")
            {
                userState.Step = RegistrationStep.None;
                await SendLanguageMenu(botClient, chatIdMsg, cancellationToken);
                return;
            }

            if (messageText is "Русский 🇷🇺" or "O'zbekcha 🇺🇿" or "English 🇬🇧" or "Qaraqalpaq tili 🇺🇿")
            {
                userState.Language = messageText switch
                {
                    "Qaraqalpaq tili 🇺🇿" => "qr",
                    "O'zbekcha 🇺🇿" => "uz",
                    "English 🇬🇧" => "en",
                    _ => "ru"
                };
                userState.Step = RegistrationStep.None;
                await SendMainMenu(botClient, chatIdMsg, userState, cancellationToken);
                return;
            }

            if (messageText is "➕ Создать команду" or "➕ Komanda dözetip" or "➕ Jamoa yaratish" or "➕ Create Team")
            {
                if (!string.IsNullOrEmpty(userState.TeamId))
                {
                    string alreadyInTeam = userState.Language switch
                    {
                        "qr" => "Siz aldın-ala komandag'a a'zo bolg'ansız!",
                        "uz" => "Siz allaqachon jamoaga a'zosiz!",
                        "en" => "You are already in a team!",
                        _ => "Вы уже состоите в команде!"
                    };
                    await botClient.SendMessage(chatIdMsg, alreadyInTeam, cancellationToken: cancellationToken);
                    return;
                }

                userState.IsLeader = true;
                await SendSubscriptionPrompt(botClient, chatIdMsg, userState, cancellationToken);
                return;
            }

            if (messageText is "🔗 Присоединиться по коду" or "🔗 Kod arqalı qosılıw" or "🔗 Kod orqali qo'shilish" or "🔗 Join by Code")
            {
                if (!string.IsNullOrEmpty(userState.TeamId))
                {
                    string alreadyInTeam = userState.Language switch
                    {
                        "qr" => "Siz aldın-ala komandag'a a'zo bolg'ansız!",
                        "uz" => "Siz allaqachon jamoaga a'zosiz!",
                        "en" => "You are already in a team!",
                        _ => "Вы уже состоите в команде!"
                    };
                    await botClient.SendMessage(chatIdMsg, alreadyInTeam, cancellationToken: cancellationToken);
                    return;
                }

                userState.IsLeader = false;
                userState.Step = RegistrationStep.AwaitingSecretCode;
                string enterCodePrompt = userState.Language switch
                {
                    "qr" => "Komandanın' jasırın kodın kiritiń:",
                    "uz" => "Jamoaning maxfiy kodini kiriting:",
                    "en" => "Enter the secret team code:",
                    _ => "Введите секретный код команды:"
                };
                await botClient.SendMessage(chatIdMsg, enterCodePrompt, cancellationToken: cancellationToken);
                return;
            }

            if (messageText is "👥 Моя команда" or "👥 Mening jamoam" or "👥 My Team" or "👥 Benim komandam")
            {
                await ShowMyTeamInfo(botClient, chatIdMsg, userState, cancellationToken);
                return;
            }

            if (messageText is "Помощь" or "Yordam" or "Help" or "Anıqlama")
            {
                string helpText = userState.Language switch
                {
                    "qr" => "📞 Anıqlama:\nTelefon: +998 90 379 29 39\nTelegram: @ronchece",
                    "uz" => "📞 Yordam:\nTelefon: +998 90 379 29 39\nTelegram: @ronchece",
                    "en" => "📞 Help:\nPhone: +998 90 379 29 39\nTelegram: @ronchece",
                    _ => "📞 Помощь:\nТелефон: +998 90 379 29 39\nTelegram: @ronchece"
                };
                await botClient.SendMessage(chatIdMsg, helpText, cancellationToken: cancellationToken);
                return;
            }

            if (messageText is "📱 Мы в социальной сети" or "📱 Ijtimoiy tarmoqlardamiz" or "📱 Social Networks" or "📱 Biz sociallıq tarmaqlarda")
            {
                await SendSocialMediaMenu(botClient, chatIdMsg, userState, cancellationToken);
                return;
            }

            // Шаги регистрации
            switch (userState.Step)
            {
                case RegistrationStep.AwaitingSecretCode:
                    string inputCode = messageText.Trim().ToUpper();
                    if (TeamsByCode.TryGetValue(inputCode, out var foundTeam))
                    {
                        userState.PendingTeamId = foundTeam.TeamId;
                        userState.Step = RegistrationStep.None;
                        await SendSubscriptionPrompt(botClient, chatIdMsg, userState, cancellationToken);
                    }
                    else
                    {
                        string invalidCode = userState.Language switch
                        {
                            "qr" => "❌ Nadurıs kod! Iltimas, kodtı qayta kiritiń:",
                            "uz" => "❌ Noto'g'ri kod! Iltimos, kodni qayta kiriting:",
                            "en" => "❌ Invalid code! Please enter the code again:",
                            _ => "❌ Неверный секретный код! Пожалуйста, проверьте и введите снова:"
                        };
                        await botClient.SendMessage(chatIdMsg, invalidCode, cancellationToken: cancellationToken);
                    }
                    break;

                case RegistrationStep.AwaitingName:
                    userState.FullName = messageText;
                    userState.Step = RegistrationStep.AwaitingPhone;
                    string phonePrompt = userState.Language switch
                    {
                        "qr" => "Telefon nomerińizdi mınaw formatta kiritiń: +998 хх ххх хх хх",
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
                            "qr" => "Format qe'te! Iltimas, nomerdi +998 хх ххх хх хх formatında kiritiń:",
                            "uz" => "Формат нотўғри! Илтимос, рақамни +998 хх ххх хх хх форматида киритинг:",
                            "en" => "Invalid format! Please enter phone number in format +998 xx xxx xx xx:",
                            _ => "Неверный формат! Введите номер строго в формате +998 хх ххх хх хх:"
                        };
                        await botClient.SendMessage(chatIdMsg, errorPhone, cancellationToken: cancellationToken);
                        return;
                    }

                    userState.PhoneNumber = messageText;

                    if (userState.IsLeader)
                    {
                        userState.Step = RegistrationStep.AwaitingTeamName;
                        string teamPrompt = userState.Language switch
                        {
                            "qr" => "Komandańızdıń atamasını kiritiń:",
                            "uz" => "Жамоангиз номини киритинг:",
                            "en" => "Enter your team name:",
                            _ => "Напишите название для своей команды:"
                        };
                        await botClient.SendMessage(chatIdMsg, teamPrompt, cancellationToken: cancellationToken);
                    }
                    else if (!string.IsNullOrEmpty(userState.PendingTeamId) && TeamsById.TryGetValue(userState.PendingTeamId, out var targetTeam))
                    {
                        // Регистрация участника
                        userState.TeamId = targetTeam.TeamId;
                        if (!targetTeam.Members.Contains(chatIdMsg))
                        {
                            targetTeam.Members.Add(chatIdMsg);
                        }
                        userState.Step = RegistrationStep.None;

                        string joinedMsg = userState.Language switch
                        {
                            "qr" => $"🎉 Siz \"{targetTeam.Name}\" komandasina qosıldın'ız!",
                            "uz" => $"🎉 Siz \"{targetTeam.Name}\" jamoasiga qo'shildingiz!",
                            "en" => $"🎉 You successfully joined the team \"{targetTeam.Name}\"!",
                            _ => $"🎉 Вы успешно присоединились к команде «{targetTeam.Name}»!"
                        };
                        await botClient.SendMessage(chatIdMsg, joinedMsg, cancellationToken: cancellationToken);

                        // Уведомление лидеру
                        string notifyLeader = $"🔔 **Новый участник в вашей команде!**\n\n👤 Имя: {userState.FullName}\n📞 Телефон: {userState.PhoneNumber}";
                        try { await botClient.SendMessage(targetTeam.LeaderChatId, notifyLeader, parseMode: ParseMode.Markdown, cancellationToken: cancellationToken); } catch { }

                        // Отчет админу
                        if (AdminChatId != 0)
                        {
                            try
                            {
                                string adminNotice = $"🚨 **Новый участник присоединился к команде!**\n\n👥 Команда: {targetTeam.Name}\n👤 Имя: {userState.FullName}\n📞 Телефон: {userState.PhoneNumber}";
                                await botClient.SendMessage(AdminChatId, adminNotice, parseMode: ParseMode.Markdown, cancellationToken: cancellationToken);
                            }
                            catch { }
                        }

                        await SendMainMenu(botClient, chatIdMsg, userState, cancellationToken);
                    }
                    break;

                case RegistrationStep.AwaitingTeamName:
                    string desiredTeamName = messageText.Trim();

                    // Проверка уникальности названия команды
                    bool exists = TeamsById.Values.Any(t => t.Name.Equals(desiredTeamName, StringComparison.OrdinalIgnoreCase));
                    if (exists)
                    {
                        string nameTaken = userState.Language switch
                        {
                            "qr" => "❌ Bul komanda ataması e'yye'm ba'nt! Basqa atama kiritiń:",
                            "uz" => "❌ Bu jamoa nomi band! Boshqa nom kiriting:",
                            "en" => "❌ This team name is already taken! Please enter another name:",
                            _ => "❌ Это название команды уже занято! Пожалуйста, введите другое название:"
                        };
                        await botClient.SendMessage(chatIdMsg, nameTaken, cancellationToken: cancellationToken);
                        return;
                    }

                    // Генерация секретного кода из 6 символов
                    string secretCode = Guid.NewGuid().ToString("N").Substring(0, 6).ToUpper();
                    string newTeamId = Guid.NewGuid().ToString("N").Substring(0, 8);

                    var newTeam = new TeamModel
                    {
                        TeamId = newTeamId,
                        SecretCode = secretCode,
                        Name = desiredTeamName,
                        LeaderChatId = chatIdMsg,
                        Members = new List<long> { chatIdMsg }
                    };

                    TeamsById[newTeamId] = newTeam;
                    TeamsByCode[secretCode] = newTeam;

                    userState.TeamId = newTeamId;
                    userState.Step = RegistrationStep.None;

                    var me = await botClient.GetMe(cancellationToken);
                    string inviteLink = $"https://t.me/{me.Username}?start=team_{newTeamId}";

                    string successLeaderMsg = userState.Language switch
                    {
                        "qr" => $"🎉 Komandag'a basshı bolıp dizimnen o'ttińiz!\n\n👥 Komanda: {newTeam.Name}\n🔐 Jasırın kod: `{secretCode}`\n👤 Atı: {userState.FullName}\n📞 Nomeri: {userState.PhoneNumber}\n\n🔗 Agzalardı chaqırıw ushın siltewiniz:\n`{inviteLink}`",
                        "uz" => $"🎉 Jamoa sardori sifatida ro'yxatdan o'tdingiz!\n\n👥 Jamoa: {newTeam.Name}\n🔐 Maxfiy kod: `{secretCode}`\n👤 Ism: {userState.FullName}\n📞 Raqam: {userState.PhoneNumber}\n\n🔗 A'zolarni taklif qilish havolasi:\n`{inviteLink}`",
                        "en" => $"🎉 Registered as Team Captain!\n\n👥 Team: {newTeam.Name}\n🔐 Secret code: `{secretCode}`\n👤 Name: {userState.FullName}\n📞 Phone: {userState.PhoneNumber}\n\n🔗 Invite link for team members:\n`{inviteLink}`",
                        _ => $"🎉 Вы успешно зарегистрировали команду как капитан!\n\n👥 Команда: {newTeam.Name}\n🔐 Секретный код: `{secretCode}`\n👤 Имя: {userState.FullName}\n📞 Номер: {userState.PhoneNumber}\n\n🔗 Пригласительная ссылка для вашей команды:\n`{inviteLink}`"
                    };

                    await botClient.SendMessage(chatIdMsg, successLeaderMsg, parseMode: ParseMode.Markdown, cancellationToken: cancellationToken);

                    // Уведомление админу
                    if (AdminChatId != 0)
                    {
                        try
                        {
                            string adminNotification = $"🚨 **Создана новая команда!**\n\n👥 Команда: {newTeam.Name}\n🔐 Код: `{secretCode}`\n👑 Капитан: {userState.FullName}\n📞 Телефон: {userState.PhoneNumber}";
                            await botClient.SendMessage(AdminChatId, adminNotification, parseMode: ParseMode.Markdown, cancellationToken: cancellationToken);
                        }
                        catch { }
                    }

                    await SendMainMenu(botClient, chatIdMsg, userState, cancellationToken);
                    break;
            }
        }

        private static async Task ShowMyTeamInfo(ITelegramBotClient botClient, long chatId, UserState user, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(user.TeamId) || !TeamsById.TryGetValue(user.TeamId, out var team))
            {
                string noTeam = user.Language switch
                {
                    "qr" => "Siz hesh qanday komandag'a a'zo emessiz.",
                    "uz" => "Siz hech qanday jamoaga a'zo emassiz.",
                    "en" => "You are not in any team.",
                    _ => "Вы пока не состоите в команде."
                };
                await botClient.SendMessage(chatId, noTeam, cancellationToken: cancellationToken);
                return;
            }

            var me = await botClient.GetMe(cancellationToken);
            string inviteLink = $"https://t.me/{me.Username}?start=team_{team.TeamId}";

            string memberList = "";
            int index = 1;
            foreach (var memberId in team.Members)
            {
                if (Users.TryGetValue(memberId, out var memberUser))
                {
                    string roleMark = memberId == team.LeaderChatId ? " (👑 Капитан)" : "";
                    memberList += $"{index}. {memberUser.FullName} - {memberUser.PhoneNumber}{roleMark}\n";
                    index++;
                }
            }

            string info = $"👥 **Команда:** {team.Name}\n🔐 **Секретный код:** `{team.SecretCode}`\n\n**Участники ({team.Members.Count}):**\n{memberList}\n🔗 **Ссылка для приглашения:**\n`{inviteLink}`";
            await botClient.SendMessage(chatId, info, parseMode: ParseMode.Markdown, cancellationToken: cancellationToken);
        }

        private static async Task SendSubscriptionPrompt(ITelegramBotClient botClient, long chatId, UserState user, CancellationToken cancellationToken)
        {
            string text = user.Language switch
            {
                "qr" => "Dizimnen o'tiw ushın tómendegi kanal ha'm betke agza bolıń:",
                "uz" => "Ro'yxatdan o'tish uchun quyidagi kanal va sahifaga obuna bo'ling:",
                "en" => "To register, please subscribe to our channel and page:",
                _ => "Для регистрации подпишитесь на наш канал и страницу:"
            };

            string checkBtnText = user.Language switch
            {
                "qr" => "✅ Agzalıqtı tekseriw",
                "uz" => "✅ Obunani tekshirish",
                "en" => "✅ Check Subscription",
                _ => "✅ Проверить подписку"
            };

            var keyboard = new InlineKeyboardMarkup(new[]
            {
                new[] { InlineKeyboardButton.WithUrl("📢 Telegram kanal", "https://t.me/ekologlar_kr") },
                new[] { InlineKeyboardButton.WithUrl("📸 Instagram", "https://www.instagram.com/ekologlar_hareketi_qr?vrfl=emh1cW93Z21ibm54") },
                new[] { InlineKeyboardButton.WithCallbackData(checkBtnText, "check_subscription") }
            });

            await botClient.SendMessage(chatId, text, replyMarkup: keyboard, cancellationToken: cancellationToken);
        }

        private static async Task SendLanguageMenu(ITelegramBotClient botClient, long chatId, CancellationToken cancellationToken)
        {
            var keyboard = new ReplyKeyboardMarkup(new[]
            {
                new KeyboardButton[] { "Русский 🇷🇺", "O'zbekcha 🇺🇿" },
                new KeyboardButton[] { "Qaraqalpaq tili 🇺🇿", "English 🇬🇧" }
            })
            {
                ResizeKeyboard = true
            };

            await botClient.SendMessage(chatId, "Выберите язык / Tilingizni tanlang / Tildi saylań / Choose language:", replyMarkup: keyboard, cancellationToken: cancellationToken);
        }

        private static async Task SendMainMenu(ITelegramBotClient botClient, long chatId, UserState user, CancellationToken cancellationToken)
        {
            string createTeamBtn = user.Language switch { "qr" => "➕ Komanda dözetip", "uz" => "➕ Jamoa yaratish", "en" => "➕ Create Team", _ => "➕ Создать команду" };
            string joinTeamBtn = user.Language switch { "qr" => "🔗 Kod arqalı qosılıw", "uz" => "🔗 Kod orqali qo'shilish", "en" => "🔗 Join by Code", _ => "🔗 Присоединиться по коду" };
            string myTeamBtn = user.Language switch { "qr" => "👥 Benim komandam", "uz" => "👥 Mening jamoam", "en" => "👥 My Team", _ => "👥 Моя команда" };
            string helpBtn = user.Language switch { "qr" => "Anıqlama", "uz" => "Yordam", "en" => "Help", _ => "Помощь" };
            string socialBtn = user.Language switch { "qr" => "📱 Biz sociallıq tarmaqlarda", "uz" => "📱 Ijtimoiy tarmoqlardamiz", "en" => "📱 Social Networks", _ => "📱 Мы в социальной сети" };

            var keyboardList = new List<KeyboardButton[]>();

            if (string.IsNullOrEmpty(user.TeamId))
            {
                keyboardList.Add(new KeyboardButton[] { createTeamBtn, joinTeamBtn });
                keyboardList.Add(new KeyboardButton[] { helpBtn, socialBtn });
            }
            else
            {
                keyboardList.Add(new KeyboardButton[] { myTeamBtn, helpBtn });
                keyboardList.Add(new KeyboardButton[] { socialBtn });
            }

            keyboardList.Add(new KeyboardButton[] { "🌐 Выбрать язык / Tilingizni tanlang / Choose language" });

            var keyboard = new ReplyKeyboardMarkup(keyboardList) { ResizeKeyboard = true };

            string text = user.Language switch
            {
                "qr" => "Bas menyu. Kerekli bólimdi saylań:",
                "uz" => "Асосий меню. Керакли бўлимни танланг:",
                "en" => "Main Menu. Choose an option:",
                _ => "Главное меню. Выберите нужный раздел:"
            };

            await botClient.SendMessage(chatId, text, replyMarkup: keyboard, cancellationToken: cancellationToken);
        }

        private static async Task SendSocialMediaMenu(ITelegramBotClient botClient, long chatId, UserState user, CancellationToken cancellationToken)
        {
            string tgText = user.Language switch { "qr" => "📢 Telegram kanal", "uz" => "📢 Telegram kanal", "en" => "📢 Telegram Channel", _ => "📢 Telegram канал" };
            string instaText = user.Language switch { "qr" => "📸 Instagram beti", "uz" => "📸 Instagram sahifa", "en" => "📸 Instagram Page", _ => "📸 Instagram аккаунт" };

            var inlineKeyboard = new InlineKeyboardMarkup(new[]
            {
                new[] { InlineKeyboardButton.WithUrl(tgText, "https://t.me/ekologlar_kr") },
                new[] { InlineKeyboardButton.WithUrl(instaText, "https://www.instagram.com/ekologlar_hareketi_qr?vrfl=emh1cW93Z21ibm54") }
            });

            string menuTitle = user.Language switch
            {
                "qr" => "Bizdin' sociallıq tarmaqlarımız:",
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