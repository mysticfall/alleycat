import math
from abc import ABC
from collections import OrderedDict
from itertools import chain
from typing import Final, Generic, TypeVar

from alleycat.reactive import RP, RV, functions as rv
from bge.types import KX_GameObject
from dependency_injector.wiring import Provide, inject
from mathutils import Euler, Vector
from rx import Observable, operators as ops

from alleycat.common import ActivatableComponent, ArgumentReader, clamp, normalize_angle
from alleycat.game import GameContext
from alleycat.input import InputMap

T = TypeVar("T", bound=KX_GameObject)


class TurretControl(Generic[T], ActivatableComponent[T], ABC):
    class ArgKeys(ActivatableComponent.ArgKeys):
        ROTATION_INPUT: Final = "Rotation Input"
        ROTATION_SENSITIVITY: Final = "Rotation Sensitivity"

    args = OrderedDict(chain(ActivatableComponent.args.items(), (
        (ArgKeys.ROTATION_INPUT, "view/rotate"),
        (ArgKeys.ROTATION_SENSITIVITY, 1.0)
    )))

    pitch: RP[float] = rv.from_value(0.0).validate(lambda _, v: clamp(v, -math.pi / 2, math.pi / 2))

    yaw: RP[float] = rv.from_value(0.0).validate(lambda _, v: normalize_angle(v))

    # noinspection PyArgumentList
    rotation: RV[Euler] = rv.combine_latest(pitch, yaw)(ops.map(lambda v: Euler((v[0], 0, -v[1]), "XYZ")))

    def __init__(self, obj: T) -> None:
        super().__init__(obj)

    @inject
    def start(
            self,
            args: dict,
            input_map: InputMap = Provide[GameContext.input.mappings]) -> None:
        super().start(args)

        props = ArgumentReader(args)

        # noinspection PyShadowingBuiltins
        input = props.require(self.ArgKeys.ROTATION_INPUT, str)
        sensitivity = props.read(self.ArgKeys.ROTATION_SENSITIVITY, float).value_or(1.0)

        self.logger.debug("args['%s'] = %s", self.ArgKeys.ROTATION_INPUT, input)
        self.logger.debug("args['%s'] = %s", self.ArgKeys.ROTATION_SENSITIVITY, self.active)

        def rotate(value: Vector):
            self.pitch += value.y * sensitivity
            self.yaw += value.x * sensitivity

        def wire(observable: Observable):
            observable.pipe(
                ops.filter(lambda _: self.active),
                ops.take_until(self.on_dispose)
            ).subscribe(rotate, on_error=self.error_handler)

        # noinspection PyTypeChecker
        props.require(self.ArgKeys.ROTATION_INPUT, str) \
            .map(lambda s: s.split("/")) \
            .bind(input_map.observe) \
            .map(wire) \
            .alt(self.logger.warning)
