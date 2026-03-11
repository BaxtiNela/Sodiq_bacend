import { FC, useEffect } from 'react'
import { DiffEditor } from '@monaco-editor/react'
import { useEditorStore } from '../store/editorStore'

export const EditorPane: FC = () => {
  const { base, current, setCurrent } = useEditorStore()

  useEffect(() => {
    // could restore from DB here
  }, [])

  return (
    <DiffEditor
      height="calc(100vh - 56px)"
      original={base}
      modified={current}
      onChange={(v) => setCurrent(v ?? '')}
      options={{
        readOnly: false,
        renderSideBySide: false,
        renderIndicators: false,
        originalEditable: false,
        diffAlgorithm: 'advanced',
        renderGutterIcons: true,
      }}
      theme='vs-dark'
      language='typescript'
    />
  )
}