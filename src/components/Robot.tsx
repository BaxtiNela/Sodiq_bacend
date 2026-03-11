import { FC, useState } from 'react'

export const Robot: FC = () => {
  const [open, setOpen] = useState(false)
  return (
    <>
      <button className='robot-fab' onClick={() => setOpen(!open)} title='Robot'>🤖</button>
      {open && (
        <div className='robot-panel'>
          <strong>Kichik robot</strong>
          <p>Ollama-ga ulanish uchun sozlamalar bu yerda bo‘ladi. (Stub)</p>
          <div style={{ display: 'flex', gap: 8 }}>
            <input placeholder='Buyruq...' style={{ flex: 1 }} />
            <button>Yuborish</button>
          </div>
        </div>
      )}
    </>
  )
}