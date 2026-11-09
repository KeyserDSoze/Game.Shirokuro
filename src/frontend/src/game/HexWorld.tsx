import { Canvas } from '@react-three/fiber'
import { OrbitControls } from '@react-three/drei'
import { useMemo } from 'react'
import { useTranslation } from 'react-i18next'
import { axialToWorld, createHexBoard, type HexCoordinate } from './hex'

type Props = {
  selected: HexCoordinate
  onSelect: (value: HexCoordinate) => void
}

function HeroToken() {
  return (
    <group position={[0, 0.24, 0]}>
      <mesh position={[0, 0.45, 0]} castShadow>
        <sphereGeometry args={[0.34, 24, 16]} />
        <meshStandardMaterial color="#faf8f2" roughness={0.8} />
      </mesh>
      <mesh position={[0, 0.92, 0]} castShadow>
        <sphereGeometry args={[0.42, 24, 16]} />
        <meshStandardMaterial color="#f7f3ec" roughness={0.75} />
      </mesh>
      <mesh position={[-0.15, 0.97, 0.37]}>
        <sphereGeometry args={[0.045, 12, 8]} />
        <meshStandardMaterial color="#252434" />
      </mesh>
      <mesh position={[0.15, 0.97, 0.37]}>
        <sphereGeometry args={[0.045, 12, 8]} />
        <meshStandardMaterial color="#252434" />
      </mesh>
      <mesh position={[0, 1.23, 0]} rotation={[0, 0, -0.15]} castShadow>
        <coneGeometry args={[0.3, 0.43, 6]} />
        <meshStandardMaterial color="#282c3f" roughness={0.85} />
      </mesh>
      <mesh position={[0, 0.46, 0.24]}>
        <sphereGeometry args={[0.12, 14, 10]} />
        <meshStandardMaterial color="#b49be7" roughness={0.5} />
      </mesh>
    </group>
  )
}

function WorldMeshes({ selected, onSelect }: Props) {
  const cells = useMemo(() => createHexBoard(2), [])
  return (
    <>
      <color attach="background" args={['#171b26']} />
      <ambientLight intensity={1.35} />
      <directionalLight position={[4, 11, 7]} intensity={3} castShadow shadow-mapSize={[1024, 1024]} />
      <pointLight position={[-5, 5, -3]} intensity={16} color="#ad9bf9" distance={14} />
      <mesh rotation={[-Math.PI / 2, 0, 0]} position={[0, -0.32, 0]} receiveShadow>
        <planeGeometry args={[200, 200]} />
        <meshStandardMaterial color="#141725" roughness={1} />
      </mesh>
      {cells.map((cell) => {
        const position = axialToWorld(cell)
        const active = cell.q === selected.q && cell.r === selected.r
        const light = (cell.q - cell.r + 12) % 2 === 0
        return (
          <mesh
            key={cell.q + ':' + cell.r}
            name={'hex-' + cell.q + '-' + cell.r}
            position={[position.x, 0, position.z]}
            rotation={[0, Math.PI / 6, 0]}
            receiveShadow
            castShadow
            onClick={(event) => { event.stopPropagation(); onSelect(cell) }}
          >
            <cylinderGeometry args={[0.98, 0.98, active ? 0.42 : 0.3, 6]} />
            <meshStandardMaterial
              color={active ? '#b7a0e9' : light ? '#e9e1d2' : '#41485b'}
              roughness={0.82}
              metalness={0.03}
            />
          </mesh>
        )
      })}
      <HeroToken />
      <OrbitControls
        target={[0, 0, 0]}
        enablePan={false}
        minDistance={6}
        maxDistance={20}
        minPolarAngle={0.2}
        maxPolarAngle={Math.PI / 2.15}
      />
    </>
  )
}

export default function HexWorld(props: Props) {
  const { t } = useTranslation()
  return (
    <div className="h-full w-full" role="img" aria-label={t('board')}>
      <Canvas
        shadows
        dpr={[1, 1.75]}
        camera={{ position: [6, 9, 11], fov: 44, near: 0.1, far: 100 }}
        fallback={<p className="p-6 text-white" role="status">{t('webglFallback')}</p>}
      >
        <WorldMeshes {...props} />
      </Canvas>
    </div>
  )
}

