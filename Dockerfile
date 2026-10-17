FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore Kooshi.ArchitectureBenchmark.slnx
RUN for project in DirectLlm WorkflowAgent ProcessBound; do \
      dotnet publish "src/Kooshi.Benchmark.${project}.Api/Kooshi.Benchmark.${project}.Api.csproj" -c Release --no-restore -o "/publish/Kooshi.Benchmark.${project}.Api"; \
    done && \
    dotnet publish src/Kooshi.Benchmark.Runner/Kooshi.Benchmark.Runner.csproj -c Release --no-restore -o /publish/runner

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS api-base
WORKDIR /app
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
ENV ASPNETCORE_URLS=http://0.0.0.0:8080 ASPNETCORE_ENVIRONMENT=Production

FROM api-base AS direct
COPY --from=build /publish/Kooshi.Benchmark.DirectLlm.Api/ .
ENTRYPOINT ["dotnet", "Kooshi.Benchmark.DirectLlm.Api.dll"]

FROM api-base AS workflow
COPY --from=build /publish/Kooshi.Benchmark.WorkflowAgent.Api/ .
ENTRYPOINT ["dotnet", "Kooshi.Benchmark.WorkflowAgent.Api.dll"]

FROM api-base AS process
COPY --from=build /publish/Kooshi.Benchmark.ProcessBound.Api/ .
ENTRYPOINT ["dotnet", "Kooshi.Benchmark.ProcessBound.Api.dll"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS benchmark
WORKDIR /benchmark
COPY Kooshi.ArchitectureBenchmark.slnx .
COPY scenarios/ scenarios/
COPY --from=build /publish/runner/ runner/
COPY --from=build /publish/Kooshi.Benchmark.DirectLlm.Api/ published/Kooshi.Benchmark.DirectLlm.Api/
COPY --from=build /publish/Kooshi.Benchmark.WorkflowAgent.Api/ published/Kooshi.Benchmark.WorkflowAgent.Api/
COPY --from=build /publish/Kooshi.Benchmark.ProcessBound.Api/ published/Kooshi.Benchmark.ProcessBound.Api/
ENV KOOSHI_PUBLISHED_API_ROOT=/benchmark/published ASPNETCORE_ENVIRONMENT=Benchmark
ENTRYPOINT ["dotnet", "/benchmark/runner/Kooshi.Benchmark.Runner.dll"]
