using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

class Program
{
    private static string BotToken = "8733826973:AAHilk5DwQIzITO05N46YDpnxwgG8grg5n8";

    // Словарь для хранения состояний пользователей (кто сейчас регистрируется)
    private static Dictionary<long, string> userStates = new Dictionary<long, string>();
    // Словарь для сохранения временных данных (имя, фамилия)
    private static Dictionary<long, string> userTempData = new Dictionary<long, string>();

    static async Task Main(string[] args)
    {
        var botClient = new TelegramBotClient(BotToken);

        using var cts = new CancellationTokenSource();

        var receiverOptions = new ReceiverOptions
        {
            AllowedUpdates = Array.Empty<UpdateType>()
        };

        botClient.StartReceiving(
            HandleUpdateAsync,
            HandleErrorAsync,
            receiverOptions,
            cts.Token
        );

        Console.WriteLine("Бот успешно запущен и ждет сообщения!");
        Console.WriteLine("Нажми Enter, чтобы остановить бота.");
        Console.ReadLine();

        cts.Cancel();
    }

    private static async Task HandleUpdateAsync(ITelegramBotClient bot, Update update, CancellationToken cancellationToken)
    {
        if (update.Message is not { } message) return;
        if (message.Text is not { } messageText) return; // Исправлено здесь

        var chatId = message.Chat.Id;
        Console.WriteLine($"[Сообщение от {message.From?.FirstName}]: {messageText}");

        // Создаем постоянную клавиатуру с кнопками снизу
        var replyKeyboard = new ReplyKeyboardMarkup(new[]
        {
            new KeyboardButton[] { "Начать", "Регистрация" },
            new KeyboardButton[] { "Поддержка" }
        })
        {
            ResizeKeyboard = true
        };

        // Проверяем, находится ли пользователь в процессе регистрации
        if (userStates.ContainsKey(chatId))
        {
            string state = userStates[chatId];

            if (state == "WAITING_FOR_NAME")
            {
                // Сохраняем введенные имя и фамилию
                userTempData[chatId] = messageText;
                userStates[chatId] = "WAITING_FOR_PHONE";

                await bot.SendMessage(
                    chatId: chatId,
                    text: "Отлично! Теперь введите ваш номер телефона в формате:\n+998 хх ххх хх хх",
                    cancellationToken: cancellationToken
                );
                return;
            }
            else if (state == "WAITING_FOR_PHONE")
            {
                // Получаем ранее сохраненное имя
                string fullName = userTempData.ContainsKey(chatId) ? userTempData[chatId] : "Не указано";
                string phone = messageText;

                // Завершаем регистрацию и сбрасываем состояние
                userStates.Remove(chatId);
                userTempData.Remove(chatId);

                await bot.SendMessage(
                    chatId: chatId,
                    text: $"✅ Регистрация успешно завершена!\n\nИмя и Фамилия: {fullName}\nНомер телефона: {phone}\n\nСпасибо!",
                    replyMarkup: replyKeyboard,
                    cancellationToken: cancellationToken
                );
                return;
            }
        }

        // Обычная обработка кнопок и команд
        switch (messageText.Trim())
        {
            case "/start":
            case "Начать":
                await bot.SendMessage(
                    chatId: chatId,
                    text: "Привет! 👋 Добро пожаловать. Выберите нужный пункт меню ниже:",
                    replyMarkup: replyKeyboard,
                    cancellationToken: cancellationToken
                );
                break;

            case "Регистрация":
                userStates[chatId] = "WAITING_FOR_NAME";
                await bot.SendMessage(
                    chatId: chatId,
                    text: "📝 Пожалуйста, введите ваши **Имя и Фамилию**:",
                    parseMode: ParseMode.Markdown,
                    cancellationToken: cancellationToken
                );
                break;

            case "Поддержка":
                await bot.SendMessage(
                    chatId: chatId,
                    text: "💬 По всем вопросам обращайтесь в поддержку: @ronchece",
                    replyMarkup: replyKeyboard,
                    cancellationToken: cancellationToken
                );
                break;

            default:
                await bot.SendMessage(
                    chatId: chatId,
                    text: "Используйте кнопки меню для навигации.",
                    replyMarkup: replyKeyboard,
                    cancellationToken: cancellationToken
                );
                break;
        }
    }

    private static Task HandleErrorAsync(ITelegramBotClient bot, Exception exception, CancellationToken cancellationToken)
    {
        Console.WriteLine($"Ошибка: {exception.Message}");
        return Task.CompletedTask;
    }
}