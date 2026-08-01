# Готовая Windows-сборка

`pgw-windows.zip` — самодостаточный `PGW.Host.exe` (.NET runtime упакован внутрь, ничего
отдельно ставить не нужно) + wwwroot + demo-конфиг + README.txt. Собрано командой:

```
dotnet publish src/PGW.Host -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true
```

Скачать: открой `pgw-windows.zip` в браузере на GitHub и нажми **Download** (или "View raw").

Это собранный бинарник, а не исходники — обновляется вручную при значимых изменениях в
`src/PGW.Host`, не на каждый коммит. Актуальность даты сборки — по времени последнего коммита,
изменившего этот файл (`git log -- dist/windows/pgw-windows.zip`).
