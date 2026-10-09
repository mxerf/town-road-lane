using System.Collections.Generic;
using Colossal;

namespace TownRoadLane.Localization
{
    public class LocalePTBR : IDictionarySource
    {
        private readonly TownRoadLaneSetting _setting;
        public LocalePTBR(TownRoadLaneSetting setting) { _setting = setting; }

        public IEnumerable<KeyValuePair<string, string>> ReadEntries(IList<IDictionaryEntryError> errors, Dictionary<string, int> indexCounts)
        {
            return new Dictionary<string, string>
            {
                { _setting.GetSettingsLocaleID(), "Town Road Lane" },
                { _setting.GetOptionTabLocaleID(TownRoadLaneSetting.kSection), "Principal" },

                { _setting.GetOptionGroupLocaleID(TownRoadLaneSetting.kEdgeGroup), "Linha de bordo junto ao meio-fio" },
                { _setting.GetOptionGroupLocaleID(TownRoadLaneSetting.kParkingGroup), "Marcações de estacionamento paralelo" },
                { _setting.GetOptionGroupLocaleID(TownRoadLaneSetting.kSegmentGroup), "Editor de marcações — divisão em segmentos" },
                { _setting.GetOptionGroupLocaleID(TownRoadLaneSetting.kSegmentDevGroup), "Divisão em segmentos — ajuste fino" },
                { _setting.GetOptionGroupLocaleID(TownRoadLaneSetting.kKeybindGroup), "Atalhos" },

                { _setting.GetOptionLabelLocaleID(nameof(TownRoadLaneSetting.EdgeLineEnabled)), "Linha de bordo em vias urbanas" },
                { _setting.GetOptionDescLocaleID(nameof(TownRoadLaneSetting.EdgeLineEnabled)),
                    "Adiciona a linha de bordo junto ao meio-fio nas vias urbanas comuns (faixas de carro de 3 m), do mesmo modo que as rodovias já têm. As alterações passam a valer depois de reiniciar o jogo." },

                { _setting.GetOptionLabelLocaleID(nameof(TownRoadLaneSetting.EdgeLineStyle)), "Estilo da linha de bordo" },
                { _setting.GetOptionDescLocaleID(nameof(TownRoadLaneSetting.EdgeLineStyle)),
                    "Malha usada só na linha de bordo automática — as linhas desenhadas com a ferramenta de marcação mantêm o próprio estilo. As opções \"G87\" exigem o mod [G87] Road Markings; sem ele, usa-se o estilo original do jogo. As alterações passam a valer depois de reiniciar o jogo." },

                { _setting.GetOptionLabelLocaleID(nameof(TownRoadLaneSetting.YellowLeftLineEnabled)), "Linha de bordo amarela à esquerda (EUA)" },
                { _setting.GetOptionDescLocaleID(nameof(TownRoadLaneSetting.YellowLeftLineEnabled)),
                    "Em cidades com tema norte-americano, vias de mão única e vias com canteiro recebem uma linha amarela na borda esquerda da pista (lado do canteiro), como nas estradas dos EUA — a linha branca continua junto ao meio-fio. Cidades com tema europeu não são afetadas. As alterações passam a valer depois de reiniciar o jogo." },

                { _setting.GetOptionLabelLocaleID(nameof(TownRoadLaneSetting.ParkingMarkingsEnabled)), "Marcar zonas de estacionamento paralelo" },
                { _setting.GetOptionDescLocaleID(nameof(TownRoadLaneSetting.ParkingMarkingsEnabled)),
                    "Desenha uma linha ao longo das zonas de estacionamento paralelo, com um traço transversal no início e no fim do quarteirão. Vias sem a subfaixa Parking Lane 2 (mão única de 3 faixas, variantes assimétricas) ficam sem marcação — a mesma cobertura da v1.1. As alterações passam a valer depois de reiniciar o jogo." },

                { _setting.GetOptionLabelLocaleID(nameof(TownRoadLaneSetting.ParkingLineStyle)), "Estilo da linha de estacionamento" },
                { _setting.GetOptionDescLocaleID(nameof(TownRoadLaneSetting.ParkingLineStyle)),
                    "Linha longitudinal ao longo da zona de estacionamento. As opções \"G87\" exigem o mod [G87] Road Markings. As alterações passam a valer depois de reiniciar o jogo." },

                { _setting.GetOptionLabelLocaleID(nameof(TownRoadLaneSetting.ParkingEndStyle)), "Estilo do traço final do estacionamento" },
                { _setting.GetOptionDescLocaleID(nameof(TownRoadLaneSetting.ParkingEndStyle)),
                    "Traço curto perpendicular no início e no fim de um quarteirão de estacionamento. \"Nenhum\" desliga os traços. As opções \"G87\" exigem o mod [G87] Road Markings. As alterações passam a valer depois de reiniciar o jogo." },

                { _setting.GetOptionLabelLocaleID(nameof(TownRoadLaneSetting.SegmentMinLengthM)), "Comprimento mínimo do segmento (m)" },
                { _setting.GetOptionDescLocaleID(nameof(TownRoadLaneSetting.SegmentMinLengthM)),
                    "Quando linhas desenhadas se cruzam, elas são divididas em segmentos (cada um pode ser ocultado ou receber outro estilo). Segmentos mais curtos que este valor se fundem com o vizinho. Mais baixo = segmentos mais finos em marcações densas; mais alto = menos lascas de linhas que só se encostam. Padrão: 1,0 m. Vale para um cruzamento na próxima edição das linhas dele, e para a cidade inteira ao recarregar o save." },

                { _setting.GetOptionLabelLocaleID(nameof(TownRoadLaneSetting.SegmentAnchorDeadZoneM)), "Zona morta em torno dos pontos-âncora (m)" },
                { _setting.GetOptionDescLocaleID(nameof(TownRoadLaneSetting.SegmentAnchorDeadZoneM)),
                    "Cruzamentos mais próximos que isso da ponta da linha não a dividem. Linhas que saem do mesmo ponto-âncora se sobrepõem no primeiro metro ou dois — sem a zona morta, essa sobreposição gera microssegmentos fantasmas. Mais baixo = divisões permitidas mais perto dos pontos; mais alto = comportamento mais estável junto às âncoras. Padrão: 2,0 m. Vale para um cruzamento na próxima edição das linhas dele, e para a cidade inteira ao recarregar o save." },

                { _setting.GetOptionLabelLocaleID(nameof(TownRoadLaneSetting.SegmentMinCrossingAngleDeg)), "Ângulo mínimo de cruzamento (°)" },
                { _setting.GetOptionDescLocaleID(nameof(TownRoadLaneSetting.SegmentMinCrossingAngleDeg)),
                    "Duas linhas que se encontram num ângulo menor que este contam como um roçar, não como um cruzamento — sem divisão. Em 0° qualquer toque divide, e linhas quase paralelas podem gerar um aglomerado de microssegmentos. Padrão: 8°. Vale para um cruzamento na próxima edição das linhas dele, e para a cidade inteira ao recarregar o save." },

                { _setting.GetOptionLabelLocaleID(nameof(TownRoadLaneSetting.SegmentHitClusterM)), "Raio de agrupamento dos cruzamentos (m)" },
                { _setting.GetOptionDescLocaleID(nameof(TownRoadLaneSetting.SegmentHitClusterM)),
                    "Um cruzamento raso aparece como vários pontos quase idênticos; pontos dentro deste raio viram uma única divisão. Mais baixo = mais desses quase duplicados sobrevivem como divisões separadas. Padrão: 1,5 m. Vale para um cruzamento na próxima edição das linhas dele, e para a cidade inteira ao recarregar o save." },

                { _setting.GetOptionLabelLocaleID(nameof(TownRoadLaneSetting.ActivateMarkingTool)), "Ativar ferramenta de marcação" },
                { _setting.GetOptionDescLocaleID(nameof(TownRoadLaneSetting.ActivateMarkingTool)),
                    "Liga ou desliga a ferramenta de marcação por cruzamento. O mesmo que o atalho abaixo, mas sempre funciona (outro mod não consegue interceptar o botão)." },

                { _setting.GetOptionLabelLocaleID(nameof(TownRoadLaneSetting.ToggleMarkingToolBinding)), "Ferramenta de marcação (atalho)" },
                { _setting.GetOptionDescLocaleID(nameof(TownRoadLaneSetting.ToggleMarkingToolBinding)),
                    "Ativa ou desativa a ferramenta de marcação por cruzamento. Padrão: Ctrl+M. Se o atalho não funcionar (outro mod o intercepta), use o botão acima." },

                { _setting.GetOptionLabelLocaleID(nameof(TownRoadLaneSetting.CycleMarkingStyleBinding)), "Trocar estilo da linha (atalho)" },
                { _setting.GetOptionDescLocaleID(nameof(TownRoadLaneSetting.CycleMarkingStyleBinding)),
                    "Com a ferramenta ativa, percorre Contínua → Tracejada → …. O estilo escolhido vale para a PRÓXIMA linha desenhada. Padrão: Y. A cor dos pontos-âncora mostra o estilo atual." },

                { _setting.GetOptionLabelLocaleID(nameof(TownRoadLaneSetting.EnterAreaModeBinding)), "Iniciar polígono de área (atalho)" },
                { _setting.GetOptionDescLocaleID(nameof(TownRoadLaneSetting.EnterAreaModeBinding)),
                    "Com um nó selecionado, inicia o modo de área poligonal: clique nos pontos-âncora para construir uma região preenchida. A mesma tecla de novo, ou Esc, cancela. Padrão: A." },

                { _setting.GetOptionLabelLocaleID(nameof(TownRoadLaneSetting.CycleAreaStyleBinding)), "Trocar estilo da área (atalho)" },
                { _setting.GetOptionDescLocaleID(nameof(TownRoadLaneSetting.CycleAreaStyleBinding)),
                    "Com a ferramenta ativa, percorre o preenchimento da PRÓXIMA área fechada (Concreto → Caixa de cruzamento → Hachura branca → Hachura amarela → Ciclovia verde → Faixa de ônibus vermelha → volta ao início). Padrão: U. Estilos G87 caem para Concreto quando o G87 não está instalado." },

                { _setting.GetEnumValueLocaleID(TownRoadLaneSetting.EdgeLineStyleEnum.WhiteSolid), "Branca contínua" },
                { _setting.GetEnumValueLocaleID(TownRoadLaneSetting.EdgeLineStyleEnum.WhiteSolidThick), "Branca contínua (larga)" },
                { _setting.GetEnumValueLocaleID(TownRoadLaneSetting.EdgeLineStyleEnum.WhiteDashed), "Branca tracejada" },
                { _setting.GetEnumValueLocaleID(TownRoadLaneSetting.EdgeLineStyleEnum.YellowSolid), "Amarela contínua" },
                { _setting.GetEnumValueLocaleID(TownRoadLaneSetting.EdgeLineStyleEnum.WhiteSolid_G87), "Branca contínua (G87)" },
                { _setting.GetEnumValueLocaleID(TownRoadLaneSetting.EdgeLineStyleEnum.WhiteDashed_G87), "Branca tracejada (G87)" },
                { _setting.GetEnumValueLocaleID(TownRoadLaneSetting.EdgeLineStyleEnum.YellowSolid_G87), "Amarela contínua (G87)" },

                { _setting.GetEnumValueLocaleID(TownRoadLaneSetting.ParkingLineStyleEnum.WhiteDashedDense), "Branca tracejada (densa)" },
                { _setting.GetEnumValueLocaleID(TownRoadLaneSetting.ParkingLineStyleEnum.WhiteDashed), "Branca tracejada" },
                { _setting.GetEnumValueLocaleID(TownRoadLaneSetting.ParkingLineStyleEnum.WhiteSolid), "Branca contínua" },
                { _setting.GetEnumValueLocaleID(TownRoadLaneSetting.ParkingLineStyleEnum.YellowDashed), "Amarela tracejada" },
                { _setting.GetEnumValueLocaleID(TownRoadLaneSetting.ParkingLineStyleEnum.YellowSolid), "Amarela contínua" },
                { _setting.GetEnumValueLocaleID(TownRoadLaneSetting.ParkingLineStyleEnum.WhiteSolid_G87), "Branca contínua (G87)" },
                { _setting.GetEnumValueLocaleID(TownRoadLaneSetting.ParkingLineStyleEnum.WhiteDashed_G87), "Branca tracejada (G87)" },
                { _setting.GetEnumValueLocaleID(TownRoadLaneSetting.ParkingLineStyleEnum.YellowSolid_G87), "Amarela contínua (G87)" },
                { _setting.GetEnumValueLocaleID(TownRoadLaneSetting.ParkingLineStyleEnum.YellowDashed_G87), "Amarela tracejada (G87)" },
                { _setting.GetEnumValueLocaleID(TownRoadLaneSetting.ParkingLineStyleEnum.BlueSolid_G87), "Azul contínua (G87)" },
                { _setting.GetEnumValueLocaleID(TownRoadLaneSetting.ParkingLineStyleEnum.BlueDashed_G87), "Azul tracejada (G87)" },

                { _setting.GetEnumValueLocaleID(TownRoadLaneSetting.ParkingEndStyleEnum.None), "Nenhum" },
                { _setting.GetEnumValueLocaleID(TownRoadLaneSetting.ParkingEndStyleEnum.WhiteSolid), "Branca contínua" },
                { _setting.GetEnumValueLocaleID(TownRoadLaneSetting.ParkingEndStyleEnum.WhiteSolidThick), "Branca contínua (larga)" },
                { _setting.GetEnumValueLocaleID(TownRoadLaneSetting.ParkingEndStyleEnum.WhiteTerminal_G87), "Linha terminal branca (G87)" },
                { _setting.GetEnumValueLocaleID(TownRoadLaneSetting.ParkingEndStyleEnum.YellowTerminal_G87), "Linha terminal amarela (G87)" },
                { _setting.GetEnumValueLocaleID(TownRoadLaneSetting.ParkingEndStyleEnum.BlueSolid_G87), "Azul contínua (G87)" },
            };
        }

        public void Unload() { }
    }
}
