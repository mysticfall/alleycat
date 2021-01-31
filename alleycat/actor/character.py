from collections import OrderedDict

import bge
from alleycat.reactive import ReactiveObject, functions as rv
from bge.types import KX_GameObject, KX_PythonComponent, KX_Scene
from bpy.types import Object
from dependency_injector.wiring import Provide, inject
from mathutils import Vector
from rx import operators as ops

from alleycat.game import GameContext
from alleycat.input import InputMap
from alleycat.log import LoggingSupport


class Character(LoggingSupport, ReactiveObject, KX_PythonComponent):
    args = OrderedDict((
        ("name", "Player"),
        ("camera", Object),
    ))

    # noinspection PyUnusedLocal
    def __init__(self, obj: KX_GameObject):
        super().__init__()

    @inject
    def start(
            self,
            args: dict,
            input_map: InputMap = Provide[GameContext.input.mappings]) -> None:
        self.name = args["name"]

        camera: Object = args["camera"]

        scene: KX_Scene = bge.logic.getCurrentScene()
        camera_obj = scene.objects.get(camera.name)

        self.logger.info("Input map: %s", input_map)
        self.logger.info("Camera: %s", type(camera_obj))

        def rotate(value: Vector):
            camera_obj.applyRotation((0, 0, -value.x), False)
            camera_obj.applyRotation((-value.y, 0, 0), True)

        def move(value: Vector):
            camera_obj.applyMovement(value.to_tuple(), True)

        rotate_input = input_map["view"]["rotate"]
        rv.observe(rotate_input.value) \
            .pipe(ops.filter(lambda v: v.length_squared > 0)) \
            .subscribe(rotate, on_error=self.error_handler)

        move_input = input_map["view"]["move"]
        rv.observe(move_input.value) \
            .pipe(ops.filter(lambda v: v.length_squared > 0), ops.map(lambda v: v.resized(3).xzy * -1)) \
            .subscribe(move, on_error=self.error_handler)

    def update(self) -> None:
        pass
