FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Копируем всё содержимое проекта
COPY . .

# Собираем и публикуем проект
RUN dotnet publish -c Release -o /app/publish

# Финальный образ для запуска
FROM mcr.microsoft.com/dotnet/runtime:8.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "TelegramBot.dll"]