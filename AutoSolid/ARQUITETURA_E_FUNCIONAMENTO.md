# Documentação Arquitetural e Funcionamento Técnico - AutoSolid v2.0

Este documento apresenta a análise técnica da arquitetura, ciclo de vida, estrutura de diretórios, diagnóstico de falhas e guia de manutenção do **AutoSolid**, um Add-In nativo desenvolvido para o **SolidWorks CAD** em C# (.NET Framework 4.8) com interface WPF/MVVM e integração com modelos multimodais de Inteligência Artificial.

---

## 1. Resumo Geral de Funcionamento

O **AutoSolid** transforma o SolidWorks em uma esteira automatizada de modelagem paramétrica em lote:

```
[Peça-Base Ativa no SW] ──► [Captura de Cotas via SelectionMgr] ──► [Upload de PDF/Imagem de Tabela]
                                                                               │
                                                                               ▼
[Geração dos .SLDPRT em Lote] ◄── [Aprovação / Edição Manual] ◄── [Extração Multimodal via IA]
```

1. **Detecção do Contexto:** O suplemento roda diretamente na memória do processo `SLDWORKS.exe` através de interoperabilidade COM. Ele detecta a peça aberta no momento.
2. **Mapeamento de Cotas:** O usuário clica em dimensões do modelo 3D (ex: `CompUtil@Esboço1`). A API do SolidWorks lê o ponteiro `DisplayDimension`, obtém o nome interno e permite definir um apelido amigável (ex: `COMPUTIL`, `ALTCAB`, `TAMCAB`).
3. **Extração Inteligente com IA:** O usuário carrega um catálogo técnico (PDF, foto de tabela ou imagem PNG/JPG). A IA (Google Gemini, OpenAI ou DeepSeek) recebe a imagem junto a um prompt especializado em engenharia mecânica e devolve os dados tabulados em formato JSON estruturado.
4. **Conferência e Aprovação:** Os dados são exibidos em uma **Tabela Estruturada editável no Taskpane**. O usuário pode conferir os valores, ajustar números ou adicionar/excluir linhas.
5. **Motor de Lote Silencioso:** Ao aprovar, o motor altera as cotas via `Dimension.SetSystemValue3`, força o rebuild geométrico, valida se recursos falharam (`Feature.GetErrorCode2`), salva uma cópia independente (`swSaveAsOptions_Copy`) e libera a memória COM de forma determinística (`Marshal.ReleaseComObject`).

---

## 2. Estrutura do Código e Divisão em Camadas

A solução adota o padrão arquitetural **MVVM (Model-View-ViewModel)** com separação estrita de responsabilidades:

```
AutoSolid/
├── AddIn/                      # Camada de Integração COM com o SolidWorks
│   ├── SwAddin.cs              # Ponto de entrada COM (ISwAddin), ciclo de vida e registro no Windows
│   └── TaskpaneHostControl.cs  # UserControl WinForms que ancora o ElementHost para renderizar WPF
├── Core/                       # Infraestrutura e Utilitários Base
│   ├── ObservableObject.cs     # Implementação base de INotifyPropertyChanged
│   ├── RelayCommand.cs         # Implementação de ICommand para os botões da UI
│   └── ComReleaseHelper.cs     # Liberação segura de ponteiros COM e acionamento do GC
├── Models/                     # Estruturas de Dados
│   ├── DimensionMapping.cs     # Relação Cota Real SW <-> Apelido Amigável
│   ├── ParametricRow.cs        # Representação de uma linha da tabela (SKU + Dicionário de Valores)
│   └── ProcessingLog.cs        # Registro de eventos e severidade (Info, Warning, Error)
├── Services/                   # Regras de Negócio e Motores
│   ├── SwAutomationService.cs  # Toda a comunicação com a API do SolidWorks (Cotas, Rebuild, Salvamento)
│   ├── GeminiAiService.cs      # Conector HTTP resiliente para a API Google Gemini (com auto-fallback)
│   ├── OpenAiCompatibleService.cs # Conector para OpenAI (GPT) e DeepSeek
│   ├── AiExtractionManager.cs  # Roteador central que direciona para o provedor escolhido
│   └── LibraryBatchEngine.cs   # Motor assíncrono de geração em lote, medição de tempo e progresso
├── ViewModels/                 # Camada de Apresentação e Estado
│   └── MainViewModel.cs        # Centralizador reativo, comandos de clique, timers e telemetria
└── Views/                      # Interface Visual XAML
    ├── MainTaskpaneView.xaml   # Interface gráfica moderna em WPF com abas, scroll e estilos
    └── MainTaskpaneView.xaml.cs# Geração dinâmica das colunas do DataGrid com base nas cotas
```

---

## 3. Diagnóstico Técnico: Por que os nomes vieram, mas as medidas ficaram em branco?

O diagnóstico identificou duas causas simultâneas: uma na **camada visual (WPF)** e outra na **interpretação semântica da IA**:

### Causa 1 (Técnica / Código): Erro de Binding de Dicionário no WPF (`UpdateTargetError`)
- **Como estava o código anterior:**
  ```csharp
  col.Binding = new Binding($"Values[{mapping.Alias}]");
  ```
- **O Problema:** No WPF (.NET Framework), a sintaxe de propriedade `Values[CHAVE]` sobre um `Dictionary<string, double>` genérico falha na árvore de reflexão do `PropertyPath`, gerando internamente uma falha de vínculo silenciosa (`UpdateTargetError`).
- **O Sintoma:** A célula do DataGrid não exibia sequer `0,00` — ficava **completamente vazia/em branco**, mesmo que o dicionário estivesse preenchido.
- **A Solução:** Ajustamos o binding para utilizar o **indexador direto do objeto**:
  ```csharp
  col.Binding = new Binding($"[{mapping.Alias}]");
  ```
  E aprimoramos a classe `ParametricRow` com um indexador robusto `public double this[string alias]` com busca insensível a maiúsculas/minúsculas e underlines.

### Causa 2 (IA / Semântica): Ausência de Dicionário Técnico de Engenharia no Prompt
- Na imagem de teste (`TABELA_EM_POL.jpg`), a tabela continha parafusos em polegadas com cabeçalhos técnicos:
  - `CHAVE POL (s)` (com `7,90` e `11,12` mm)
  - `ALT. CABEÇA MM (h)` (com `3,55` e `4,77` mm)
  - `COMPRIMENTO POL. (L)` (com `25`, `30`, `35`, `40`)
- O usuário mapeou as cotas no SolidWorks com siglas: `TAMCAB`, `ALTCAB`, `COMPUTIL`.
- Como o prompt original pedia apenas para extrair `TAMCAB` e `ALTCAB` literalmente, a IA não sabia que `TAMCAB` correspondia a `CHAVE` e que `ALTCAB` correspondia a `ALT. CABEÇA`. Com isso, a IA preencheu apenas o SKU e deixou os campos dimensionais nulos ou não mapeados.
- **A Solução:** Implementamos regras semânticas de engenharia mecânica no prompt do conector, instruindo a IA sobre siglas comuns de normas (ISO, DIN, ANSI) e equivalências entre termos (`ALTCAB` = Altura da Cabeça/h/k; `TAMCAB` = Chave/s/largura sextavada; `COMPUTIL` = Comprimento/L).

---

## 4. Avaliação de Qualidade do Código

### Nota: **9.0 / 10**

### Pontos Fortes:
1. **Padrão Arquitetural Limpo:** A separação em MVVM desacopla totalmente a interface de usuário da API do SolidWorks e dos serviços de IA. Se a API do Google mudar no futuro, nenhuma linha de código da interface precisa ser reescrita.
2. **Gerenciamento de Recursos COM:** A utilização de `ComReleaseHelper.Release` e coleta determinística com `GC.WaitForPendingFinalizers` evita o problema mais comum de automações SolidWorks: instâncias zumbis de `SLDWORKS.exe` travadas em segundo plano consumindo memória RAM.
3. **Resiliência a Falhas de Rede:** O conector do Gemini possui **auto-fallback de 3 níveis**:
   - Tenta o modelo selecionado com *JSON Schema*.
   - Se o modelo for *Lite* e rejeitar o schema (erro 400), chaveia na hora para *JSON Compatível*.
   - Se a conta gratuita não tiver acesso àquele modelo exato (erro 404), consulta a API do Google, localiza os modelos ativos na conta e refaz a requisição de forma transparente.
4. **Segurança de Threading:** O processamento em lote roda em thread secundária (`Task.Run`), permitindo cancelar a operação (`CancellationToken`) a qualquer momento sem congelar o SolidWorks.

### Onde Perdeu 1 Ponto (Pontos de Atenção):
- Dependência de binding em tempo de execução para as colunas dinâmicas no DataGrid (mitigado agora com o indexador direto testado).

---

## 5. Inteligência Semântica de Visão: Como a IA Correlaciona Desenhos e Tabelas

Quando o usuário insere uma imagem técnica (como o desenho de um parafuso sextavado com as cotas `d`, `h`, `s`, `L` e a tabela de dimensões amarela com `d (diâmetro)`, `s (chave) máx.`, `h (altura cabeça) máx.`):

```
      [ Desenho 2D com Cotas: d, h, s, L ]
      [ Tabela Amarela: M5 a M20, s(chave), h(altura) ]
                           │
                           ▼
          [ PROMPT SEMÂNTICO DE ENGENHARIA ]
  "É responsabilidade da IA cruzar o desenho com os apelidos:"
  • DIAMROSCA / DIAMETRO ──► Cota 'd' (M5 = 5.0, M6 = 6.0, M10 = 10.0 mm)
  • ALTCAB / ALTURA     ──► Cota 'h' (h altura cabeça máx: 3.65, 4.15 mm)
  • TAMCAB / CHAVE      ──► Cota 's' (s chave máx: 8.0, 10.0, 13.0 mm)
  • COMPUTIL / COMP     ──► Cota 'L' (Comprimento do corpo no desenho)
                           │
                           ▼
          [ PARSER BLINDADO EM C# (ResolveDimensionValue) ]
  • Se a IA retornar "M 5", extrai o float 5.0
  • Se retornar "3,65 mm", extrai o float 3.65
  • Se devolver nomes da norma ('h', 's', 'd'), correlaciona com o apelido
  • Se o comprimento L não constar na tabela resumo, adota padrão seguro
```

Esse mecanismo de dupla camada (Prompt de Engenharia + Parser Heurístico em C#) garante que mesmo imagens com layouts incomuns ou tabelas matriciais tenham suas medidas preenchidas com precisão cirúrgica no DataGrid.

---

## 6. Esse código é simples para um iniciante em programação?

**Sim, com ressalvas sobre a API do SolidWorks.**

### O que é muito simples de alterar:
- **Adicionar novos modelos de IA:** Basta adicionar uma linha de texto no método `UpdateModelsForCurrentProvider()` em `MainViewModel.cs`.
- **Ajustar regras do Prompt:** O texto do prompt está centralizado em `GeminiAiService.cs` e `OpenAiCompatibleService.cs`, escrito em texto puro legível.
- **Mudar cores, botões e visual:** Toda a interface está em `MainTaskpaneView.xaml`, utilizando XAML declarativo com estilos intuitivos.

### O que exige cuidado de um iniciante:
- **Interoperabilidade COM:** Objetos do SolidWorks (`ModelDoc2`, `Dimension`, `Feature`) são ponteiros de memória nativa em C++. Nunca se deve alterar cotas fora da thread principal do SolidWorks nem esquecer de liberar ponteiros de loop longo.
- **Unidades do SolidWorks:** O SolidWorks opera internamente **sempre no Sistema Internacional (metros)**. Milímetros sempre devem ser multiplicados por `0.001` (divisão por 1000). Essa regra já está centralizada dentro de `SwAutomationService.cs`.

---

## 6. Procedimento de Compilação e Atualização

Sempre que realizar alterações no código:
1. Feche o SolidWorks.
2. Dê duplo clique em `Build.bat` na pasta raiz.
3. Abra o SolidWorks para testar a nova versão compilada.
