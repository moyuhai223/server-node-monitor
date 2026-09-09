import { BarChart, LineChart } from 'echarts/charts'
import { DataZoomComponent, GridComponent, LegendComponent, MarkLineComponent, TooltipComponent } from 'echarts/components'
import * as echarts from 'echarts/core'
import { UniversalTransition } from 'echarts/features'
import { CanvasRenderer } from 'echarts/renderers'

echarts.use([TooltipComponent, GridComponent, LegendComponent, DataZoomComponent, MarkLineComponent, BarChart, LineChart, CanvasRenderer, UniversalTransition])

export { default as VChart } from 'vue-echarts'
export { echarts }
