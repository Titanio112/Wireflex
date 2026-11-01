# Wireflex

[![Última release](https://img.shields.io/github/v/release/Titanio112/Wireflex?include_prereleases&label=release)](https://github.com/Titanio112/Wireflex/releases)
[![Licença MIT](https://img.shields.io/badge/licen%C3%A7a-MIT-blue)](LICENSE)
[![Build](https://github.com/Titanio112/Wireflex/actions/workflows/build.yml/badge.svg)](https://github.com/Titanio112/Wireflex/actions/workflows/build.yml)

[Português](#português) · [English](#english)

---

## Português

> **Versão beta 2.0.0-beta.1, para testes.** A versão final ainda não foi lançada. Podem aparecer problemas; se aparecer, [avise](#como-reportar-um-problema).

O Wireflex mostra a tela do celular Android no PC, usando o [scrcpy](https://github.com/Genymobile/scrcpy). Ele fica no ícone da bandeja do Windows e conecta sozinho quando você pluga o cabo USB ou quando o celular está no mesmo Wi-Fi. Funciona com vários celulares ao mesmo tempo.

[![Baixar a beta](https://img.shields.io/badge/Baixar-a%20beta-1F6FEB?style=for-the-badge)](https://github.com/Titanio112/Wireflex/releases)

### Como instalar

Precisa do Windows 10 ou 11 (64 bits).

1. **Instale o adb e o scrcpy.** Abra o PowerShell e rode:
   ```powershell
   winget install --id Google.PlatformTools
   winget install --id Genymobile.scrcpy
   ```
   Depois saia e entre de novo na sessão do Windows (ou reinicie o PC).
2. **Baixe o zip** `Wireflex-2.0.0-beta.1-win-x64.zip` na [página de releases](https://github.com/Titanio112/Wireflex/releases) e extraia numa pasta fixa.
3. **Confira o SHA-256.** No PowerShell, na pasta do download:
   ```powershell
   Get-FileHash .\Wireflex-2.0.0-beta.1-win-x64.zip -Algorithm SHA256
   ```
   O resultado tem que ser igual ao das notas da release (e ao do arquivo `.sha256`).
4. **Abra o `Wireflex.App.exe`.** Se aparecer o aviso do SmartScreen, clique em **Mais informações** e depois em **Executar assim mesmo** (o programa ainda não é assinado). Nenhuma janela abre: o Wireflex aparece como um ícone azul na bandeja, perto do relógio.

No celular, ative as "Opções do desenvolvedor" e a "Depuração USB". Na primeira vez que plugar o cabo, aceite a pergunta de confiar no computador. O `LEIA-ME.txt` do zip tem mais detalhes.

### O que funciona
- Espelhamento automático ao plugar o cabo.
- Troca para o Wi-Fi ao tirar o cabo e volta para o cabo ao plugar de novo (o Wi-Fi é ligado pelo cabo na primeira vez).
- Vários celulares ao mesmo tempo, cada um na sua janela.
- Menu de cada celular: abrir agora, pausar, desativar e esquecer.
- Modo de conexão: automático, perguntar antes de abrir ou nunca.
- Se você fechou a janela, ela não reabre sozinha até o celular sair do alcance e voltar.
- Reconexão do Wi-Fi depois de reiniciar o PC; reabre algumas vezes se o espelhamento cair.
- Iniciar com o Windows (liga e desliga no menu).
- Log que esconde o número de série dos celulares e os endereços IP.

### Problemas conhecidos
- **Avisos e perguntas repetidos com celular que "dorme" no Wi-Fi** ([#44](https://github.com/Titanio112/Wireflex/issues/44)). Com a tela apagada, alguns celulares saem e voltam do Wi-Fi várias vezes por dia, e cada volta gera um aviso (e, no modo perguntar, uma pergunta nova). Se incomodar, use o modo automático ou desative esse celular no menu.
- **Janela fecha logo depois de tirar o cabo** ([#45](https://github.com/Titanio112/Wireflex/issues/45)). Às vezes a janela que abre pelo Wi-Fi fecha sozinha poucos segundos depois e não volta. Use "Abrir agora" no menu do celular.

### Próximas versões
v2.1:
- Conexão pela "Depuração sem fio" do Android 11+ (mDNS), sem precisar do cabo na primeira vez.
- Encontrar o celular (vibrar, tocar um som).
- Temas.
- Janela de configurações.
- Editar o nome do celular.

### Como reportar um problema
Abra uma [issue](https://github.com/Titanio112/Wireflex/issues) contando o que fez, o que esperava e o que aconteceu, com o horário aproximado. Anexe o log do dia, que fica em `%LOCALAPPDATA%\Wireflex\logs`. O log já esconde o número de série e o IP dos celulares; mesmo assim, dê uma olhada antes de enviar.

### Créditos
- [scrcpy](https://github.com/Genymobile/scrcpy), da Genymobile: o espelhamento de tela.
- [scrcpy-auto-switch](https://github.com/Titanio112/scrcpy-auto-switch): o projeto de origem (v1), do qual o Wireflex é uma reescrita.

### Licença
[MIT](LICENSE)

[↑ Ir para English](#english)

---

## English

> **Beta version 2.0.0-beta.1, for testing.** The final version has not been released yet. You may run into problems; if you do, [let us know](#how-to-report-a-problem).

Wireflex shows your Android phone's screen on your PC, using [scrcpy](https://github.com/Genymobile/scrcpy). It lives in the Windows tray and connects on its own when you plug in the USB cable or when the phone is on the same Wi-Fi. It works with several phones at once.

[![Download the beta](https://img.shields.io/badge/Download-the%20beta-1F6FEB?style=for-the-badge)](https://github.com/Titanio112/Wireflex/releases)

### How to install

You need Windows 10 or 11 (64-bit).

1. **Install adb and scrcpy.** Open PowerShell and run:
   ```powershell
   winget install --id Google.PlatformTools
   winget install --id Genymobile.scrcpy
   ```
   Then sign out and back in to Windows (or restart the PC).
2. **Download the zip** `Wireflex-2.0.0-beta.1-win-x64.zip` from the [releases page](https://github.com/Titanio112/Wireflex/releases) and extract it to a permanent folder.
3. **Check the SHA-256.** In PowerShell, in the download folder:
   ```powershell
   Get-FileHash .\Wireflex-2.0.0-beta.1-win-x64.zip -Algorithm SHA256
   ```
   The result must match the one in the release notes (and in the `.sha256` file).
4. **Open `Wireflex.App.exe`.** If SmartScreen shows a warning, click **More info** and then **Run anyway** (the program is not signed yet). No window opens: Wireflex shows up as a blue icon in the tray, near the clock.

On the phone, turn on "Developer options" and "USB debugging". The first time you plug in the cable, accept the prompt to trust the computer. The `LEIA-ME.txt` in the zip (in Portuguese) has more details.

### What works
- Automatic mirroring when you plug in the cable.
- Switches to Wi-Fi when you unplug the cable and back to the cable when you plug it in again (Wi-Fi is turned on through the cable the first time).
- Several phones at once, each in its own window.
- Menu for each phone: open now, pause, disable and forget.
- Connection mode: automatic, ask before opening, or never.
- If you close the window, it does not reopen on its own until the phone leaves and comes back.
- Wi-Fi reconnects after restarting the PC; reopens a few times if mirroring drops.
- Start with Windows (on/off in the menu).
- Log that hides the phones' serial numbers and IP addresses.

### Known issues
- **Repeated notices and questions with a phone that "sleeps" on Wi-Fi** ([#44](https://github.com/Titanio112/Wireflex/issues/44)). With the screen off, some phones drop off and rejoin Wi-Fi several times a day, and each return shows a notice (and, in ask mode, a new question). If it bothers you, use automatic mode or disable that phone in the menu.
- **Window closes right after unplugging the cable** ([#45](https://github.com/Titanio112/Wireflex/issues/45)). Sometimes the window that opens over Wi-Fi closes by itself a few seconds later and does not come back. Use "Abrir agora" (open now) in the phone's menu.

### Coming next
v2.1:
- Connection through Android 11+ "Wireless debugging" (mDNS), with no cable needed the first time.
- Find my phone (vibrate, play a sound).
- Themes.
- Settings window.
- Edit the phone's name.

### How to report a problem
Open an [issue](https://github.com/Titanio112/Wireflex/issues) describing what you did, what you expected and what happened, with the approximate time. Attach that day's log, found in `%LOCALAPPDATA%\Wireflex\logs`. The log already hides the phones' serial numbers and IPs; still, take a look before sending it.

### Credits
- [scrcpy](https://github.com/Genymobile/scrcpy) by Genymobile: the screen mirroring.
- [scrcpy-auto-switch](https://github.com/Titanio112/scrcpy-auto-switch): the original project (v1), which Wireflex rewrites from scratch.

### License
[MIT](LICENSE)

[↑ Ir para Português](#português)
