import { useEffect } from 'react'
import { LeftToolbar } from './components/LeftToolbar'
import { BottomBar } from './components/BottomBar'
import { EditorPane } from './components/EditorPane'
import { Robot } from './components/Robot'
import './index.css'

function App() {
  useEffect(() => {
    // show robot on load
  }, [])
  return (
    <div className='app-root'>
      <aside className='left-toolbar'>
        <LeftToolbar />
      </aside>
      <main className='main-pane'>
        <EditorPane />
      </main>
      <footer className='bottom-bar'>
        <BottomBar />
      </footer>
      <Robot />
    </div>
  )
}

export default App
