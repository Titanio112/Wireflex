# Release v2.0.0-beta.1 (passo a passo)

Primeira versão para testadores, como `.zip` portátil no GitHub Releases. A release é publicada à mão, **depois do merge** do PR da Etapa 3 na `main`.

## 1. Gerar o pacote a partir da `main`

O executável leva o commit na versão (`2.0.0-beta.1+<commit>`). Por isso o zip e o SHA-256 mudam a cada commit: gere de novo depois do merge, não reaproveite um zip feito no branch.

```powershell
git switch main
git pull
dotnet test Wireflex.slnx -c Release
& .\tools\release\New-WireflexRelease.ps1 -Version 2.0.0-beta.1
```

O script:
- publica o app self-contained para win-x64 num único `Wireflex.App.exe`, sem símbolos de depuração;
- monta `artifacts\release\Wireflex-2.0.0-beta.1-win-x64.zip` com `Wireflex.App.exe` e `LEIA-ME.txt`;
- confere que o zip tem só esses dois arquivos, sem `.git`, config, logs ou símbolos, e que o executável não tem caminho pessoal de build;
- grava `Wireflex-2.0.0-beta.1-win-x64.zip.sha256` e mostra o SHA-256.

Antes de subir, abra o zip numa pasta nova e confira: o app abre, o ícone aparece na bandeja e o LEIA-ME está legível.

## 2. Criar a tag

```powershell
git tag -a v2.0.0-beta.1 -m "Wireflex 2.0.0-beta.1"
git push origin v2.0.0-beta.1
```

## 3. Criar a release (pre-release)

Pelo site: **Releases > Draft a new release**, escolha a tag `v2.0.0-beta.1`, título `Wireflex 2.0.0-beta.1`, marque **Set as a pre-release**, anexe o `.zip` e o `.sha256` e cole as notas abaixo, com o SHA-256 gerado.

Ou pela linha de comando, salvando antes o modelo da seção 4 (com o SHA-256) em `notas.md`, fora do repositório. O comando cria a release como rascunho; publique depois de revisar:

```powershell
gh release create v2.0.0-beta.1 --repo Titanio112/Wireflex --prerelease --draft `
  --title "Wireflex 2.0.0-beta.1" --notes-file notas.md `
  artifacts\release\Wireflex-2.0.0-beta.1-win-x64.zip `
  artifacts\release\Wireflex-2.0.0-beta.1-win-x64.zip.sha256
```

## 4. Notas da release (modelo)

```markdown
Primeira versão de testes do Wireflex 2: a tela do celular Android no PC, conectando sozinha pelo cabo ou pelo Wi-Fi.

**É uma beta.** Podem aparecer problemas; avise abrindo uma issue com o log do dia anexado (o log já mascara número de série e IP).

### Como usar
1. Instale o adb e o scrcpy: `winget install --id Google.PlatformTools` e `winget install --id Genymobile.scrcpy`.
2. Baixe o `Wireflex-2.0.0-beta.1-win-x64.zip`, extraia numa pasta fixa e abra o `Wireflex.App.exe`.
3. Se o Windows mostrar o SmartScreen, clique em "Mais informações" > "Executar assim mesmo" (o executável ainda não é assinado).
4. O Wireflex fica no ícone da bandeja. O `LEIA-ME.txt` do zip tem os detalhes.

### O que funciona
- App de bandeja, sem janela, uma instância só.
- Espelhamento automático ao plugar o cabo; troca para o Wi-Fi ao tirar o cabo e volta para o cabo ao plugar (Wi-Fi ligado pelo cabo, porta 5555).
- Vários celulares ao mesmo tempo, cada um com sua janela.
- Menu por celular: abrir agora, pausar/retomar, desativar, esquecer.
- Modo de conexão: automático, perguntar antes de abrir (com uma janelinha de pergunta) ou nunca.
- "Fechou a janela, não reabre" até o celular sair do alcance e voltar.
- Reconexão do Wi-Fi depois de reiniciar o PC ou o servidor do adb; reabre poucas vezes se o espelhamento cair.
- Iniciar com o Windows (liga e desliga no menu).
- Logs com número de série e IP mascarados.

### O que ainda não funciona
- Depuração sem fio do Android 11+ (pareamento/mDNS): o Wi-Fi ainda é ligado pelo cabo na primeira vez.
- Encontrar o celular, temas, janela de configurações, editar o nome do celular.
- Perfis do scrcpy por celular, notificação com botões, início em horário agendado.
- Instalador, atualização automática e executável assinado.

### Problemas conhecidos
- **Avisos e perguntas repetidos com celular que "dorme" no Wi-Fi** ([#44](https://github.com/Titanio112/Wireflex/issues/44)): com a tela apagada, alguns celulares saem e voltam do Wi-Fi várias vezes por dia. Cada vez aparece o aviso "não responde (offline)" e, no modo "perguntar antes de abrir", a pergunta volta, mesmo depois de um "Não". Se incomodar, use o modo automático ou desative esse celular no menu.
- **Janela fecha logo depois de tirar o cabo** ([#45](https://github.com/Titanio112/Wireflex/issues/45)): às vezes a janela que abre pelo Wi-Fi fecha sozinha poucos segundos depois e não volta. Use "Abrir agora" no menu do celular.

### Verificação
SHA-256 do `Wireflex-2.0.0-beta.1-win-x64.zip`:

    <cole aqui o SHA-256 gerado no passo 1>

Para conferir no PowerShell: `Get-FileHash .\Wireflex-2.0.0-beta.1-win-x64.zip -Algorithm SHA256`
```
