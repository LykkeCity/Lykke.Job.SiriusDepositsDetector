FROM mcr.microsoft.com/dotnet/aspnet:5.0
RUN apt-get install -y libc6-dev
WORKDIR /app
COPY . .
ENTRYPOINT ["dotnet", "Lykke.Job.SiriusDepositsDetector.dll"]
