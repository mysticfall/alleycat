from dependency_injector import providers
from dependency_injector.containers import DeclarativeContainer
from dependency_injector.providers import Configuration, Dependency, Factory, FactoryAggregate, Singleton

from alleycat.event import EventLoopScheduler
from alleycat.input import AxisBinding, Input, InputBinding, InputMap, KeyInputSource, KeyPressInput, MouseAxisInput, \
    MouseButtonInput, MouseInputSource, TriggerBinding


class InputContext(DeclarativeContainer):
    config: Configuration = Configuration()

    scheduler: providers.Provider[EventLoopScheduler] = Dependency(instance_of=EventLoopScheduler)

    keyboard: providers.Provider[KeyInputSource] = Singleton(KeyInputSource, scheduler)

    mouse: providers.Provider[MouseInputSource] = Singleton(MouseInputSource, scheduler)

    binding_factory: providers.Provider[InputBinding] = FactoryAggregate(
        trigger=Factory(TriggerBinding.from_config),
        axis=Factory(AxisBinding.from_config))

    input_factory: providers.Provider[Input] = FactoryAggregate(
        key_press=Factory(KeyPressInput.from_config, keyboard),
        mouse_button=Factory(MouseButtonInput.from_config, mouse),
        mouse_axis=Factory(MouseAxisInput.from_config, mouse))

    mappings: providers.Provider[InputMap] = Singleton(InputMap.from_config, binding_factory, input_factory, config)
