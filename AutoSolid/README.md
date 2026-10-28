# AutoSolid - Add-In Nativo para SolidWorks com IA Multimodal

O **AutoSolid** é um Add-In nativo de automação CAD para o **SolidWorks** (.NET Framework 4.8 / C#), hospedado diretamente na barra lateral de ferramentas (**Taskpane**) com interface moderna em **WPF (XAML)** e arquitetura MVVM.

Ele evolui a lógica desenvolvida no AutoSolidParaf (que alterava cotas de parafusos com base em um arquivo JSON estático) para uma solução universal: permite abrir qualquer peça-template no SolidWorks, capturar visualmente as cotas na viewport ou árvore de projetos, extrair tabelas de especificações a partir de **PDFs técnicos ou imagens usando Inteligência Artificial multimodal (Gemini)** com **Structured Outputs (JSON Schema)** e gerar centenas de peças derivadas .sldprt em lote no modo silencioso.

---

## 🏗 Arquitetura do Projeto e Estrutura de Arquivos

Para entender como esses arquivos conversam entre si, pense no **AutoSolid** como uma linha de montagem baseada no padrão **MVVM (Model-View-ViewModel)**:

1. **A Porta de Entrada (AddIn):** O SolidWorks inicia e chama o SwAddin.cs, que abre o painel lateral através do TaskpaneHostControl.cs.
2. **A Interface (Views):** A tela moderna em WPF (MainTaskpaneView.xaml) é exibida ao usuário, recebendo cliques, arquivos arrastados e dados da tabela.
3. **O Cérebro (ViewModels):** O MainViewModel.cs escuta tudo o que acontece na tela, toma as decisões de negócio e orquestra as tarefas.
4. **Os Especialistas (Services):** O cérebro chama os serviços para o trabalho pesado — o GeminiAiService lê PDFs com Inteligência Artificial, o SwAutomationService mexe nas cotas 3D do SolidWorks e o LibraryBatchEngine automatiza a fila de peças.
5. **Os Dados e Suporte (Models e Core):** Guardam os dados em trânsito (linhas de tabela, cotas, logs) e garantem que a memória do Windows seja limpa sem travar o SolidWorks.

---

### 🗂️ Raiz da Solução e Scripts de Instalação

* **AutoSolid.sln**  
  É o arquivo da **Solução do Visual Studio**. Ele funciona como um índice mestre que reúne todos os projetos, referências e configurações de compilação da aplicação em um só lugar.

* **RegisterAddIn.bat**  
  Script de linha de comando para registrar o suplemento no Windows via ferramenta RegAsm (64-bit). Como o SolidWorks é um software nativo (COM/C++) e o AutoSolid é feito em .NET (C#), esse script cria as chaves no Registro do Windows avisando ao SolidWorks: *Existe um suplemento novo pronto para ser carregado*.

* **UnregisterAddIn.bat**  
  Script oposto ao de registro. Ele remove as referências e chaves do suplemento do Registro do Windows. É útil para quando você for desinstalar, trocar o local da DLL ou limpar versões antigas em testes.

---

### 📦 Projeto AutoSolid (/AutoSolid)

* **AutoSolid.csproj**  
  Arquivo de configuração do projeto C#. Ele define que a saída gerada será uma biblioteca de classes (.dll), estipula a versão do .NET Framework (4.8 — compatível com o SolidWorks) e lista todas as dependências externas e bibliotecas COM vinculadas.

#### ⚙️ Properties/
* **AssemblyInfo.cs**  
  Contém as informações de identidade do seu programa (versão, nome, autor). O mais importante aqui é a configuração [assembly: ComVisible(true)] e o Guid, que atribuem uma identidade universal única para que o ecossistema Windows/SolidWorks consiga enxergar a DLL.

#### 🔌 AddIn/ (Ponte de Conexão com o SolidWorks)
* **SwAddin.cs**  
  É o **ponto de partida** do seu suplemento. Ele implementa a interface oficial da SolidWorks API (ISwAddin). Quando o SolidWorks abre, ele executa o método ConnectToSW deste arquivo (onde você cria menus, botões e registra o Add-in) e, quando fecha, executa o DisconnectFromSW para desligar tudo com segurança.

* **TaskpaneHostControl.cs**  
  O SolidWorks foi construído sobre uma tecnologia legada (WinForms), mas as telas modernas são feitas em WPF (XAML). Este arquivo cria um controle intermediário (UserControl) que serve como uma moldura (via ElementHost) para permitir encaixar a sua tela moderna dentro do painel lateral do SolidWorks.

#### 🧠 Core/ (Mecanismos Básicos e Infraestrutura)
* **ObservableObject.cs**  
  Implementa a interface INotifyPropertyChanged. Na prática, ele funciona como um sistema de aviso: toda vez que um valor muda no código C# (por exemplo, o status de processamento de uma peça), ele notifica a tela na hora para que ela se atualize automaticamente, sem você precisar mexer no layout manualmente.

* **RelayCommand.cs**  
  Conecta as ações do usuário na tela (como o clique de um botão) diretamente a um método do C# no ViewModel, sem precisar misturar código de lógica dentro do arquivo visual (.xaml.cs). Ele também permite habilitar ou desabilitar botões automaticamente dependendo do estado da aplicação.

* **ComReleaseHelper.cs**  
  O SolidWorks usa objetos COM gerenciados em memória C++. Se o C# não avisar que parou de usar uma peça ou cota, o SolidWorks pode travar ou continuar consumindo memória RAM mesmo após fechar. Esta classe garante a liberação forçada e limpa desses ponteiros de memória e do Garbage Collector.

#### 📊 Models/ (Estruturas e Modelagem de Dados)
* **DimensionMapping.cs**  
  Representa o elo de tradução de medidas. Ele mapeia o nome técnico da cota dentro do SolidWorks (como D1@Esboço1) para um nome amigável e legível pelo usuário (como D_EXT ou Comprimento).

* **ParametricRow.cs**  
  Representa uma linha da tabela de automação. Guarda informações como o código/SKU da variação, a lista de valores dinâmicos daquelas cotas e o status atual da linha (por exemplo: *Pendente*, *Em Processamento*, *Sucesso* ou *Erro*).

* **ProcessingLog.cs**  
  Estrutura que armazena os eventos do sistema. Cada log guarda a hora exata, a gravidade (Informação, Aviso, Erro) e a mensagem explicativa, permitindo que o usuário acompanhe o progresso em tempo real numa caixa de saída visual.

#### 🛠️ Services/ (Trabalhadores e Integrações)
* **SwAutomationService.cs**  
  É o braço mecânico do suplemento dentro do 3D. Ele contém o código que fala diretamente com a API do SolidWorks para encontrar cotas no modelo aberto, alterar seus valores via código (SetSystemValue3), reconstruir a geometria (EditRebuild3) e disparar salvamentos.

* **GeminiAiService.cs**  
  Serviço de Inteligência Artificial. Ele envia imagens técnicas, folhas de catálogo ou PDFs para a API do Google Gemini e força o retorno em um formato estruturado (JSON Schema). É quem extrai automaticamente dimensões e tabelas de documentos brutos para preencher a sua grade.

* **LibraryBatchEngine.cs**  
  O motor de automação em lote. Ele roda em segundo plano (de forma assíncrona para não congelar o SolidWorks) e coordena a fila: pega cada variação da tabela, injeta os valores nas cotas, reconstrói o modelo 3D, exporta para novos arquivos (como STEP, PDF ou SLDPRT) e registra o histórico de logs.

#### 🎛️ ViewModels/ (Regras de Negócio e Controle)
* **MainViewModel.cs**  
  É o maestro da aplicação. Ele não desenha nada na tela e nem mexe direto no SolidWorks: ele apenas guarda o estado atual (tabelas, comandos ativos, botões liberados) e chama os Services na hora certa. Ele faz a ponte entre a interface visual e os motores de automação.

#### 🖥️ Views/ (Interface com o Usuário)
* **MainTaskpaneView.xaml**  
  O design visual da barra lateral em si. Escrito em XAML/WPF, define a disposição dos botões modernos, paleta de cores, abas, caixa de logs, a tabela dinâmica (DataGrid) e a área de arrastar arquivos (DropZone).

* **MainTaskpaneView.xaml.cs**  
  O código de suporte exclusivo da interface (code-behind). Cuida de eventos específicos da janela que dependem da renderização física, como detectar quando o usuário soltou um arquivo na área de arrastar (Drag & Drop) e ajustar a criação dinâmica das colunas da tabela conforme os dados chegam.

---

## 🔄 Comparação: De onde viemos vs. Onde estamos

| Recurso | AutoSolidParaf (Anterior) | AutoSolid (Novo Add-In Nativo) |
|---|---|---|
| **Tipo de Aplicação** | Executável Externo WinForms (.exe) | Add-In Nativo no Taskpane Lateral (.dll COM) |
| **Interface** | Windows Forms básico | WPF Moderno com XAML, MVVM, Cards e DataGrid |
| **Peça Base** | Caminho fixo gravado em código | Detecta qualquer peça .sldprt ativa no SolidWorks |
| **Cotas** | Nomes hardcoded (DiamRosca@Esboço1) | Captura interativa via clique e SelectionManager |
| **Entrada de Dados** | Arquivo Parametros.json manual em disco | Leitura inteligente de **PDF/Imagem via IA Gemini** ou edição direta no DataGrid |
| **Structured Output** | N/A | IA gera estritamente o JSON com as colunas mapeadas |
| **Tratamento de Geometria** | ForceRebuild3 sem checagem | Verificação pós-rebuild com Feature.GetErrorCode2 |
| **Gerenciamento de Memória** | Finalização do processo | Marshal.ReleaseComObject determinístico por ciclo |

---

## 🚀 Como Registrar e Rodar no SolidWorks

### 1. Compilação
O projeto já está compilado em in\Debug\AutoSolid.dll. Se fizer alterações no código:
- Abra AutoSolid.sln no Visual Studio e compile (Ctrl + Shift + B), ou execute no terminal:
  `powershell
  & C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe C:\SW_Master\Automations\AutoSolid\AutoSolid.sln /p:Configuration=Debug
  `

### 2. Registrar o Add-In no Windows
- Clique com o botão direito em RegisterAddIn.bat e escolha **Executar como Administrador**.
- O script executa o egasm.exe 64-bit com a flag /codebase e adiciona as chaves do SolidWorks no Registro do Windows.

### 3. Abrir o SolidWorks
1. Inicie o SolidWorks.
2. Na barra lateral direita (onde ficam a Biblioteca de Projetos e Propriedades), você verá o ícone do **AutoSolid**.
3. Se não aparecer de imediato, vá em **Ferramentas > Suplementos (Add-Ins)** e marque a caixa de seleção do **AutoSolid**.

---

## 💡 Como Usar

### Aba 1: Template e Mapeamento
1. Abra sua peça base (ex: parafuso, flange, bucha, perfil).
2. O Add-In exibe o nome do arquivo ativo no topo.
3. No SolidWorks, dê um duplo clique no recurso para exibir as cotas, ou selecione a cota desejada.
4. Clique em **+ Capturar Cota Selecionada**. O Add-In captura o nome interno (ex: D1@Esboço1) e sugere um apelido.
5. Edite o apelido na coluna Apelido (IA) se desejar (ex: D_EXT, COMPRIMENTO, PASSO).
6. Defina a pasta onde os arquivos gerados serão salvos.
7. Clique em **Avançar para Entrada de Dados (IA) ➔**.

### Aba 2: Dados e Geração com IA
1. **Escolha o Provedor de IA:**
   - **Google Gemini:** Modelos recomendados: gemini-3.8-flash, gemini-2.5-flash, gemini-2.5-pro, gemini-1.5-flash, gemini-1.5-pro.
     - *Dica para Conta Gratuita:* Clique no botão **🔄 Listar da Conta** para que o Add-In consulte o Google AI Studio e liste automaticamente todos os modelos disponíveis na sua chave!
   - **OpenAI (GPT):** gpt-4o-mini, gpt-4o, o3-mini, o1-mini, gpt-4-turbo.
   - **DeepSeek:** deepseek-chat (DeepSeek-V3), deepseek-reasoner (DeepSeek-R1), deepseek-coder.
2. Insira a respectiva chave de API (Gemini, OpenAI ou DeepSeek).
3. Arraste e solte o PDF de catálogo técnico ou a imagem da tabela de medidas na área tracejada.
4. Clique em **⚡ Extrair Dados da Tabela com IA**.
5. A IA analisará o documento e preencherá automaticamente o DataGrid com o SKU e todas as dimensões mapeadas.
6. Ajuste ou adicione linhas manualmente se necessário.
7. Clique em **🚀 Gerar Biblioteca em Lote**.
8. O motor de lote atualizará cada cota, forçará a reconstrução geométrica, verificará se houve erro de recurso e salvará cada arquivo .sldprt silenciosamente.