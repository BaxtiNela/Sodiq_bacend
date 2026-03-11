import { FC } from 'react'
import { useEditorStore } from '../store/editorStore'

export const BottomBar: FC = () => {
  const { hasPending, acceptAll, revertAll } = useEditorStore()
  return (
    <div style={{ display: 'flex', width: '100%', alignItems: 'center', gap: 12 }}>
      <div>
        {hasPending ? <span>O‘zgarishlar kutilmoqda</span> : <span>Toza ishchi nusxa</span>}
      </div>
      <div style={{ marginLeft: 'auto', display: 'flex', gap: 8 }}>
        <button className='cancel-btn' onClick={revertAll}>Bekor qilish</button>
        <button className='accept-btn' onClick={acceptAll}>Qabul qilish</button>
      </div>
    </div>
  )
}