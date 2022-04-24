from typing import Any, Final, Iterator, Mapping, Type, TypeVar

from returns.maybe import Maybe, Nothing
from returns.result import Failure, ResultE
from validator_collection import dict as dict_type, not_empty

from alleycat.common import InvalidTypeError, maybe_type, require_type

T = TypeVar("T")


class MapReader(Mapping[str, Any]):
    source: Final[Mapping[str, Any]]

    def __init__(self, source: Mapping[str, Any]) -> None:
        super().__init__()

        self.source = dict_type(source, allow_empty=True)

    def read(self, key: str, tpe: Type[T]) -> Maybe[T]:
        return maybe_type(self.source[key], tpe) if not_empty(key) in self.source else Nothing

    def require(self, key: str, tpe: Type[T]) -> ResultE[T]:
        value = self.source[key] if not_empty(key) in self.source else None

        if value is None:
            return Failure(ValueError(f"Missing required argument '{key}'."))

        return require_type(self.source[key], tpe).alt(
            lambda _: InvalidTypeError(f"Argument '{key}' has an invalid value: '{self[key]}'."))

    def __getitem__(self, key: str):
        return self.source[key]

    def __len__(self) -> int:
        return len(self.source)

    def __iter__(self) -> Iterator[Any]:
        return self.source.__iter__()
