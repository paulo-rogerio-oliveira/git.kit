# git.kit

Aplicativo **WPF (.NET 10, MVVM)** para **replicar um commit de um branch em outro**
dentro de um repositório git, via **cherry-pick** ou **integração de diff**. Quando o
merge não pode ser concluído automaticamente, o usuário é direcionado para resolver os
conflitos no **TortoiseGit**.

## Funcionalidades

1. Informar uma **URL** ou um **caminho local** e clicar em **Iniciar**. O campo é um
   **combo editável** que **lista os repositórios já utilizados** e vem **pré-preenchido com
   o último** (histórico em `%LOCALAPPDATA%\git.kit\recent-repositories.json`):
   - **URL** → o repositório é preparado via **cache local**: na primeira vez cria-se um
     **espelho** (`git clone --mirror`) em `%LOCALAPPDATA%\git.kit\cache`, registrado no
     índice `cache-index.json`; nas próximas vezes o espelho é **atualizado**
     (`git remote update`) em vez de reclonado do zero. A **pasta de trabalho** é então
     clonada **a partir do espelho local** (bem mais ágil) e o `origin` é reapontado para
     a URL real (para o push). Se o cache falhar, cai no clone direto do remote. A pasta de
     trabalho usa caminho curto (`<unidade>\gtk\<n>`, ex.: `C:\gtk\1`; fallback `%TEMP%\gtk`)
     para evitar estourar o limite de caminho do Windows.
   - **Caminho local** → valida se é um repositório git e o **clona na mesma pasta de
     trabalho**, operando sempre na cópia — assim o projeto original **não troca de
     branch** nem tem a árvore de trabalho alterada. A URL do remote original é exibida.
   Nos dois casos os **branches são carregados** automaticamente.
   As pastas de trabalho de **sessões anteriores** são **limpas em background** ao abrir o
   app (o app apenas lista/remove o que já existia no início — as cópias criadas durante o
   uso atual nunca são removidas).
2. Listar **branches** (locais e remotos) e escolher a **origem**. O **destino** é um
   campo **editável**: selecione um branch existente **ou digite um nome novo**.
   - Há um **filtro** para pesquisar os branches de origem pelo nome.
   - Se o branch de destino **não existir**, ele é **criado automaticamente**: a partir de
     **develop** quando o nome termina em `dev`, caso contrário a partir de **master**
     (com fallback para `main`).
3. Listar os **commits** do branch de origem e escolher qual replicar (com **filtro** por
   hash, autor ou mensagem).
4. Replicar usando uma de duas estratégias:
   - **Cherry-pick** (`git cherry-pick -x`) — replica o commit preservando autoria/mensagem.
   - **Integração de diff** (`git diff … | git apply --3way`) — aplica as diferenças e
     gera um novo commit.
5. Em caso de **conflito** que o git não resolve automaticamente, abre-se um
   **formulário de conflitos** com um grid (arquivo, tipo de conflito, código e status).
   Cada linha tem um botão **Resolver** que abre o **TortoiseGitMerge** para o arquivo;
   ao marcar como resolvido no TortoiseGit e voltar ao formulário, o status muda para
   **Resolvido** (atualização automática ao focar a janela, ou via "Atualizar status").
   Quando todos estiverem resolvidos, **Concluir replicação** finaliza
   (`cherry-pick --continue` ou commit). Se o TortoiseGit não for localizado, o usuário é
   convidado a **selecionar o executável** (`TortoiseGitProc.exe`/`TortoiseGitMerge.exe`).
6. **Enviar o branch via push** (`git push -u origin <branch>`) após a replicação, com
   confirmação do destino. O upstream é sempre o **remote real do repositório originário**:
   ao clonar de um caminho local, o `origin` da cópia temporária é reapontado para a URL
   do remote do repositório de origem (se o repositório local não tiver remote, o push
   retorna ao próprio caminho de origem).
7. **Log** de todos os comandos git executados — exibido na tela e também **gravado em
   arquivo**, numa pasta separada por sessão (`%LOCALAPPDATA%\git.kit\logs\git-<timestamp>.log`).

Toda a integração com git é feita pela **CLI** (`git`), encapsulada em `ProcessRunner`.

## Estrutura

```
GitKit.slnx
├── src/
│   ├── GitKit.Core/          # Lógica de domínio, sem dependência de UI
│   │   ├── Models/           # GitBranch, GitCommit, ReplicationMode, ReplicationResult, ...
│   │   └── Services/         # IProcessRunner/ProcessRunner, IGitService/GitService,
│   │                         # ITortoiseGitLauncher/TortoiseGitLauncher
│   └── GitKit.App/           # WPF + MVVM
│       ├── MVVM/             # ObservableObject, RelayCommand, AsyncRelayCommand
│       ├── Services/         # IDialogService/DialogService, ConflictResolutionCoordinator,
│       │                     # WorkspaceService (pastas de trabalho + limpeza),
│       │                     # GitCommandLogger (log em arquivo)
│       ├── ViewModels/       # MainViewModel, ConflictsViewModel, ConflictItemViewModel
│       └── Views/            # MainWindow.xaml, ConflictsWindow.xaml
└── tests/
    └── GitKit.Core.Tests/    # Testes de integração contra repositórios git temporários
```

A separação **Core ⇄ App** mantém toda a lógica de git testável sem WPF. O `MainViewModel`
recebe `IGitService`, `ConflictResolutionCoordinator`, `IDialogService` e `WorkspaceService`
por injeção (composição manual feita em `App.xaml.cs`, onde também são ligados o
`GitCommandLogger` e a limpeza das pastas de trabalho).

## Pré-requisitos

- [.NET SDK 10](https://dotnet.microsoft.com/) (testado com 10.0.203)
- **git** disponível no `PATH`
- **TortoiseGit** instalado (opcional — necessário apenas para a resolução manual de conflitos)

## Como compilar e executar

```powershell
# Compilar tudo
dotnet build GitKit.slnx

# Rodar os testes (cria repositórios git temporários)
dotnet test tests/GitKit.Core.Tests/GitKit.Core.Tests.csproj

# Executar a aplicação WPF
dotnet run --project src/GitKit.App/GitKit.App.csproj
```

## Distribuição (CI/CD)

O **CI** (`.github/workflows/ci.yml`) compila e roda os testes em cada push/PR (Windows).

O **CD** (`.github/workflows/release.yml`) dispara em tags `v*` e publica um **executável
único** self-contained (`git.kit-<versão>-win-x64.exe`, via
`PublishSingleFile`/`IncludeNativeLibrariesForSelfExtract`) — o usuário final não precisa
ter o .NET instalado. Junto do executável é distribuído **somente o manual em PDF**
(`docs/manual-do-usuario.pdf`), anexado ao GitHub Release.

O PDF é a versão impressa de `docs/manual-do-usuario.html` (desenhado para A4). Para
regenerá-lo após editar o HTML, imprima-o para PDF (ex.: com o Edge headless):

```powershell
& "$env:ProgramFiles (x86)\Microsoft\Edge\Application\msedge.exe" `
  --headless=new --disable-gpu --no-pdf-header-footer `
  --print-to-pdf="docs\manual-do-usuario.pdf" `
  "file:///$((Resolve-Path docs\manual-do-usuario.html).Path -replace '\\','/')"
```

## Fluxo de uso

1. Informe a **URL** (clone numa pasta temporária) **ou um caminho local** de um
   repositório git e clique em **Iniciar** — os branches são carregados automaticamente.
2. Selecione o **branch de origem** (os commits são carregados automaticamente). No
   **branch de destino**, escolha um existente ou **digite um nome novo** — branches
   inexistentes são criados a partir de develop (sufixo `dev`) ou master.
3. Escolha a **estratégia** (cherry-pick ou integração de diff).
4. Selecione o commit na lista e clique em **Replicar commit**.
5. Se houver conflito, o **formulário de conflitos** abre automaticamente: clique em
   **Resolver** em cada arquivo (abre o TortoiseGitMerge), marque como resolvido, use
   **Atualizar status** e então **Concluir replicação**. O botão **Resolver conflitos...**
   na janela principal reabre o formulário enquanto houver pendência.
6. Clique em **Enviar branch (push)** para publicar o branch de destino no `origin`
   (uma confirmação mostra o remote alvo antes do envio).
