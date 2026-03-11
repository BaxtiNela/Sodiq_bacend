import { FC } from 'react'

export const LeftToolbar: FC = () => {
  return (
    <div>
      <button className='toolbar-btn' title='Explorer'>📁</button>
      <button className='toolbar-btn' title='Search'>🔎</button>
      <button className='toolbar-btn' title='SCM'>🪵</button>
      <button className='toolbar-btn' title='Run'>▶️</button>
      <button className='toolbar-btn' title='Get Shit Done'>⚡</button>
    </div>
  )
}