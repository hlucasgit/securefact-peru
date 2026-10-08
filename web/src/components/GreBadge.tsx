import type { GreState } from '../api/types'
import { GRE_LABELS, GRE_TONES } from '../lib/format'
import { Badge } from './ui'

export function GreBadge({ state }: { state: GreState }) {
  return <Badge tone={GRE_TONES[state]}>{GRE_LABELS[state]}</Badge>
}
