FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Копируем абсолютно весь проект
COPY . .

# Сразу собираем и публикуем без лишних команд restore
RUN dotnet publish -c Release -o /app/publish

# Берем легкий образ для запуска
FROM mcr.microsoft.com/dotnet/runtime:8.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "TelegramBot.dll"]