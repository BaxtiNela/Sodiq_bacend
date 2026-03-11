import { create } from 'zustand'

interface State {
  base: string
  current: string
  hasPending: boolean
  setCurrent: (v: string) => void
  acceptAll: () => void
  revertAll: () => void
}

export const useEditorStore = create<State>((set, get) => ({
  base: `function hello(name: string) {\n  return 'Hello, ' + name\n}\n`,
  current: `function hello(name: string) {\n  return 'Hello, ' + name + '!'\n}\n`,
  hasPending: true,
  setCurrent: (v) => set({ current: v, hasPending: v !== get().base }),
  acceptAll: () => set(({ current }) => ({ base: current, hasPending: false })),
  revertAll: () => set(({ base }) => ({ current: base, hasPending: false })),
}))